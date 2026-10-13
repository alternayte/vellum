using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.Ai.Usage;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Ai;

/// <param name="SupportsStructuredOutput">
/// True when the provider's own structured-output mode gave a valid answer, false when the
/// provider rejected that mode or gave an answer outside the schema, null when the test could
/// not tell.
/// </param>
public sealed record AiConnectionTest(
    bool Ok, long LatencyMs, string Model, string? ReportedModel, bool? SupportsStructuredOutput, ConnectionTestError? Error);

/// <summary>
/// The provider-neutral entry to every AI call. It routes the task to the provider in the
/// workspace's settings, applies that provider's settings, budgets and rate limit, meters
/// each provider call, and returns a typed result. With no provider or no key configured it
/// returns <see cref="AiFailure.NotConfigured"/>; it never throws for a provider fault.
/// Model output is untrusted: only an answer that is valid against the request's schema
/// becomes a value.
/// </summary>
public sealed class AiClient(
    IServiceProvider services, SettingsResolver settings, SecretReader secrets, AiUsageLedger ledger, ILogger<AiClient> logger)
{
    /// <summary>The corrected answers a provider is asked for after an invalid one.</summary>
    public const int MaxRepairs = 2;
    public const string TestPurpose = "test-connection";

    private sealed record Route(AiProviderSettings Provider, IAiProviderAdapter Adapter, ProviderTarget Target, decimal? WorkspaceBudgetUsd);

    /// <summary>The provider calls of one request, added up.</summary>
    private sealed class Totals(Route route)
    {
        private int _calls;
        private int? _input = 0, _output = 0;
        private decimal? _cost = 0m;
        private string? _reportedModel;

        public void Add(ProviderReply reply, decimal? cost)
        {
            _calls++;
            _input = _input is null || reply.InputTokens is null ? null : _input + reply.InputTokens;
            _output = _output is null || reply.OutputTokens is null ? null : _output + reply.OutputTokens;
            _cost = _cost is null || cost is null ? null : _cost + cost;
            _reportedModel = reply.ReportedModel ?? _reportedModel;
        }

        public string? ReportedModel => _reportedModel;

        public AiUsage ToUsage() => new(
            route.Provider.Id, route.Provider.Kind, route.Provider.Model, _reportedModel, _calls, _input, _output, _cost);
    }

    /// <summary>
    /// Asks for one JSON value and returns it as <typeparamref name="T"/> when it is valid
    /// against <see cref="AiRequest.JsonSchema"/>. An invalid answer is sent back for
    /// correction at most <see cref="MaxRepairs"/> times.
    /// </summary>
    /// <exception cref="ArgumentException">The request has no schema, or the schema is not a JSON Schema.</exception>
    public async Task<AiResult<T>> CompleteStructuredAsync<T>(AiRequest request, CancellationToken ct = default)
    {
        if (request.JsonSchema is not { } schemaJson || StructuredOutput.Compile(schemaJson) is not { } schema)
            throw new ArgumentException("A structured request needs a JSON Schema.", nameof(request));

        var (route, unroutable) = await RouteAsync(request.WorkspaceId, request.Task, ct);
        if (route is null)
            return new AiResult<T>.Failed(unroutable!);

        var native = route.Provider.SupportsStructuredOutput;
        var prompt = PromptAssembler.Build(request, askForJsonInPrompt: !native);
        var totals = new Totals(route);
        var messages = prompt.Messages;
        IReadOnlyList<string> issues = [];

        for (var attempt = 0; attempt <= MaxRepairs; attempt++)
        {
            var (reply, ticket, failure) = await CallAsync(route, request, TaskName(request.Task), prompt, messages, native, ct);
            if (reply is null)
                return new AiResult<T>.Failed(failure!);

            if (Failure(reply, request.MaxOutputTokens) is { } stopped)
            {
                await EndAsync(route, request, ticket!, reply, Outcome(reply), totals);
                return new AiResult<T>.Failed(stopped);
            }

            issues = schema.Check<T>(reply.Text, lenient: !native, out var value);
            await EndAsync(route, request, ticket!, reply, issues.Count == 0 ? "succeeded" : "invalid_output", totals);
            if (issues.Count == 0)
                return new AiResult<T>.Succeeded(value!, totals.ToUsage());

            messages = prompt.WithRepair(issues);
        }

        return new AiResult<T>.Failed(new AiFailure.InvalidOutput(issues));
    }

    /// <summary>
    /// Streams the answer as text. A provider whose settings switch streaming off is called
    /// once and the whole answer is one item. A failure ends the stream with
    /// <see cref="AiStreamFailedException"/>; a cancelled token ends it with
    /// <see cref="OperationCanceledException"/> and aborts the HTTP request.
    /// </summary>
    public async IAsyncEnumerable<string> StreamTextAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (route, unroutable) = await RouteAsync(request.WorkspaceId, request.Task, ct);
        if (route is null)
            throw new AiStreamFailedException(unroutable!);

        var prompt = PromptAssembler.Build(request, askForJsonInPrompt: false);
        var totals = new Totals(route);

        if (!route.Provider.SupportsStreaming)
        {
            var (whole, wholeTicket, refused) = await CallAsync(route, request, TaskName(request.Task), prompt, prompt.Messages, native: false, ct);
            if (whole is null)
                throw new AiStreamFailedException(refused!);

            await EndAsync(route, request, wholeTicket!, whole, Outcome(whole), totals);
            if (Failure(whole, request.MaxOutputTokens) is { } stopped)
                throw new AiStreamFailedException(stopped);

            yield return whole.Text;
            yield break;
        }

        var (ticket, admission) = await ledger.BeginAsync(Start(route, request, TaskName(request.Task), prompt, prompt.Messages, null), ct);
        if (ticket is null)
            throw new AiStreamFailedException(admission!);

        var providerRequest = new ProviderRequest(route.Target, prompt.System, prompt.Messages, null, SchemaName(request.Purpose), request.MaxOutputTokens);
        ProviderReply? end = null;
        try
        {
            await foreach (var item in route.Adapter.StreamAsync(providerRequest, ct))
            {
                if (item is ProviderStreamEvent.Delta delta)
                    yield return delta.Text;
                else if (item is ProviderStreamEvent.End last)
                    end = last.Reply;
            }
        }
        finally
        {
            // No terminal event was read: the caller cancelled or stopped reading.
            if (end is null)
                await EndAsync(route, request, ticket, null, "cancelled", totals);
        }

        if (end is null)
            throw new AiStreamFailedException(new AiFailure.ProviderError("stream_interrupted", "The provider's stream ended before it was complete."));

        await EndAsync(route, request, ticket, end, Outcome(end), totals);
        if (Failure(end, request.MaxOutputTokens) is { } failure)
            throw new AiStreamFailedException(failure);
    }

    /// <summary>
    /// Makes a small real call to one provider entry through the same pipeline, metered with
    /// the purpose <c>test-connection</c>. The entry can be switched off; its kind must be
    /// allowed by the instance policy.
    /// </summary>
    /// <returns>Null when the workspace has no provider with that ID.</returns>
    public async Task<AiConnectionTest?> TestProviderAsync(Guid workspaceId, Guid providerId, string actorId, CancellationToken ct = default)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var instance = await settings.GetInstanceAsync(ct);
        if (workspace.Effective.Ai.Providers.FirstOrDefault(p => p.Id == providerId) is not { } provider)
            return null;

        AiConnectionTest Fail(string code, string message, long latency = 0, string? reported = null) =>
            new(false, latency, provider.Model, reported, null, new ConnectionTestError(code, message));

        if (!instance.Policy.AllowedAiProviderKinds.Contains(provider.Kind))
            return Fail("disabled_by_policy", $"The instance policy does not allow the provider kind '{provider.Kind}'.");

        var (route, unroutable) = await TargetAsync(workspaceId, provider, instance.Policy, workspace, ct);
        if (route is null)
        {
            return unroutable is AiFailure.ProviderError error
                ? Fail(error.Code, error.Message)
                : Fail("not_configured", "The provider needs a model name, a base URL where the kind has none of its own, and an API key in the workspace secrets.");
        }

        var schemaJson = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
            ["required"] = new JsonArray("ok"),
            ["additionalProperties"] = false,
        });
        var request = new AiRequest(
            workspaceId, AiTask.Explain, TestPurpose, "test-connection/1", "This is a connection test.",
            [new AiMessage(AiRole.User, [AiPart.Instruction("Reply with the JSON object {\"ok\": true} and nothing else.")])],
            schemaJson, MaxOutputTokens: 1024, actorId);
        var schema = StructuredOutput.Compile(schemaJson)!;
        var totals = new Totals(route);
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        AiConnectionTest Refused(AiFailure failure) => failure switch
        {
            AiFailure.BudgetExceeded { Reason: "price_unknown" } => Fail("budget_exceeded", "A budget is set and the model has no price, so the budget cannot be kept. Add a price for the model or remove the budget."),
            AiFailure.BudgetExceeded => Fail("budget_exceeded", "The monthly AI budget is used up."),
            AiFailure.RateLimited => Fail("rate_limited", "The provider's requests-per-minute limit in settings is reached. Try again in a minute."),
            _ => Fail("cancelled", "The test was cancelled."),
        };

        // First the provider's own structured-output mode. A rejected request is tried once
        // more as plain text, which tells an unsupported mode from a broken connection.
        bool? structured = null;
        foreach (var native in (bool[])[true, false])
        {
            var prompt = PromptAssembler.Build(request, askForJsonInPrompt: !native);
            var (reply, ticket, failure) = await CallAsync(route, request, "test", prompt, prompt.Messages, native, ct);
            if (reply is null)
                return Refused(failure!);

            var valid = reply.Error is null && reply.Stop == ProviderStop.Completed
                && schema.Check<JsonElement>(reply.Text, lenient: !native, out _).Count == 0;
            await EndAsync(route, request, ticket!, reply, reply.Error is null && reply.Stop == ProviderStop.Completed && !valid ? "invalid_output" : Outcome(reply), totals);

            if (reply.Error is { Code: "invalid_request" } && native)
            {
                structured = false;
                continue;
            }

            if (reply.Error is { } error)
                return Fail(error.Code, error.Message, Elapsed(), totals.ReportedModel);

            // The provider answered. A refusal or a cut answer still proves the connection.
            if (native && reply.Stop == ProviderStop.Completed)
                structured = valid;
            return new AiConnectionTest(true, Elapsed(), provider.Model, totals.ReportedModel, structured, null);
        }

        // Unreachable: the plain-text pass returns in every branch.
        throw new UnreachableException();
    }

    private async Task<(Route? Route, AiFailure? Failure)> RouteAsync(Guid workspaceId, AiTask task, CancellationToken ct)
    {
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var instance = await settings.GetInstanceAsync(ct);
        var routing = workspace.Effective.Ai.TaskRouting;
        var providerId = task switch
        {
            AiTask.InferArchitecture => routing.InferArchitecture,
            AiTask.ProposeFlow => routing.ProposeFlow,
            AiTask.Critique => routing.Critique,
            _ => routing.Explain,
        };

        if (providerId is null)
        {
            // The effective document drops a route to a provider that is switched off or that
            // the policy no longer allows. The stored choice tells that case from no choice.
            return (null, StoredRoute(workspace, task) is null ? new AiFailure.NotConfigured() : new AiFailure.Disabled());
        }

        if (workspace.Effective.Ai.Providers.FirstOrDefault(p => p.Id == providerId) is not { } provider)
            return (null, new AiFailure.NotConfigured());
        if (!provider.Enabled)
            return (null, new AiFailure.Disabled());

        return await TargetAsync(workspaceId, provider, instance.Policy, workspace, ct);
    }

    private static Guid? StoredRoute(WorkspaceSettings workspace, AiTask task)
    {
        var name = JsonNamingPolicy.CamelCase.ConvertName(task.ToString());
        if (workspace.Overrides["ai"] is JsonObject ai && ai["taskRouting"] is JsonObject stored && stored.TryGetPropertyValue(name, out var value))
            return value is JsonValue text && text.TryGetValue<string>(out var id) && Guid.TryParse(id, out var guid) ? guid : null;

        var defaults = workspace.Defaults.Ai.TaskRouting;
        return task switch
        {
            AiTask.InferArchitecture => defaults.InferArchitecture,
            AiTask.ProposeFlow => defaults.ProposeFlow,
            AiTask.Critique => defaults.Critique,
            _ => defaults.Explain,
        };
    }

    private async Task<(Route? Route, AiFailure? Failure)> TargetAsync(
        Guid workspaceId, AiProviderSettings provider, InstancePolicy policy, WorkspaceSettings workspace, CancellationToken ct)
    {
        var adapter = services.GetServices<IAiProviderAdapter>().FirstOrDefault(a => a.Kind == provider.Kind);
        if (adapter is null || string.IsNullOrWhiteSpace(provider.Model))
            return (null, new AiFailure.NotConfigured());

        // The settings module decides the destination: it applies the instance policy and
        // gives the origin the stored key is bound to. The call goes to that URL and no other.
        Uri baseUrl;
        switch (workspace.ProviderBaseUrl(provider.Id))
        {
            case SettingsUrl.Usable usable:
                baseUrl = usable.Url;
                break;
            case SettingsUrl.BlockedByPolicy blocked:
                return (null, new AiFailure.ProviderError("url_refused", blocked.Message));
            default:
                return (null, new AiFailure.NotConfigured());
        }

        string? apiKey;
        try
        {
            switch (await secrets.ReadAsync(workspaceId, SecretNames.AiApiKey(provider.Id), baseUrl, ct))
            {
                case SecretRead.Found found:
                    apiKey = found.Value;
                    break;
                case SecretRead.SavedForAnotherHost other:
                    // A key never follows a changed base URL to a new host.
                    return (null, new AiFailure.ProviderError("key_saved_for_another_host",
                        $"The API key was saved for {other.BoundOrigin ?? "another host"}. Enter it again to use it with {other.RequestedOrigin}."));
                default:
                    apiKey = null;
                    break;
            }
        }
        catch (SecretUnavailableException)
        {
            return (null, new AiFailure.ProviderError("secret_unavailable",
                "The stored API key cannot be decrypted with the loaded master key. Save the key again."));
        }

        if (apiKey is null && adapter.RequiresApiKey)
            return (null, new AiFailure.NotConfigured());

        var target = new ProviderTarget(
            provider.Id, provider.Kind, provider.Model, baseUrl, apiKey is null ? null : adapter.Credential(apiKey),
            policy.AllowPrivateNetworkUrls, TimeSpan.FromSeconds(provider.TimeoutSeconds), provider.Temperature);
        return (new Route(provider, adapter, target, workspace.Effective.Usage.MonthlyAiBudgetUsd), null);
    }

    private static CallStart Start(
        Route route, AiRequest request, string task, AssembledPrompt prompt, IReadOnlyList<ProviderMessage> messages, JsonElement? schema) =>
        new(request.WorkspaceId, route.Provider.Id, route.Provider.Kind, route.Provider.Model, task, request.Purpose,
            request.PromptTemplateVersion, request.ActorId, route.Provider.MonthlyBudgetUsd, route.WorkspaceBudgetUsd,
            route.Provider.RequestsPerMinute, prompt.ByteCount(messages, schema), request.MaxOutputTokens);

    /// <summary>Admits and makes one provider call. A null reply means no call was made or it was cancelled.</summary>
    private async Task<(ProviderReply? Reply, CallTicket? Ticket, AiFailure? Failure)> CallAsync(
        Route route, AiRequest request, string task, AssembledPrompt prompt, IReadOnlyList<ProviderMessage> messages, bool native,
        CancellationToken ct)
    {
        var schema = native ? request.JsonSchema : null;
        try
        {
            var (ticket, refused) = await ledger.BeginAsync(Start(route, request, task, prompt, messages, schema), ct);
            if (ticket is null)
                return (null, null, refused);

            try
            {
                var reply = await route.Adapter.CompleteAsync(
                    new ProviderRequest(route.Target, prompt.System, messages, schema, SchemaName(request.Purpose), request.MaxOutputTokens), ct);
                return (reply, ticket, null);
            }
            catch (OperationCanceledException)
            {
                await EndAsync(route, request, ticket, null, "cancelled", null);
                return (null, null, new AiFailure.Cancelled());
            }
        }
        catch (OperationCanceledException)
        {
            return (null, null, new AiFailure.Cancelled());
        }
    }

    private async Task EndAsync(Route route, AiRequest request, CallTicket ticket, ProviderReply? reply, string outcome, Totals? totals)
    {
        var errorCode = reply?.Error?.Code;
        var cost = await ledger.CompleteAsync(ticket, new CallEnd(
            outcome, errorCode, reply?.InputTokens, reply?.OutputTokens, reply?.ReportedModel, reply?.Error?.Rejected ?? false));
        if (reply is not null)
            totals?.Add(reply, cost);

        // Labels and counts only. No prompt text, no answer text and no credential is logged.
        logger.Log(outcome == "succeeded" ? LogLevel.Information : LogLevel.Warning,
            "AI call {ProviderKind} {Task} {Purpose} ended {Outcome} {ErrorCode}",
            route.Provider.Kind, request.Task, request.Purpose, outcome, errorCode ?? "-");
    }

    private static string Outcome(ProviderReply reply) => reply switch
    {
        { Error.Code: "timeout" } => "timed_out",
        { Error.Code: "rate_limited" } => "rate_limited",
        { Error: not null } => "provider_error",
        { Stop: ProviderStop.Refused } => "refused",
        { Stop: ProviderStop.Truncated } => "truncated",
        _ => "succeeded",
    };

    /// <summary>The typed failure of a reply that is not a complete answer, or null for a complete one.</summary>
    private static AiFailure? Failure(ProviderReply reply, int maxOutputTokens) => reply switch
    {
        { Error.Code: "timeout" } => new AiFailure.TimedOut(),
        { Error: { Code: "rate_limited" } limited } => new AiFailure.RateLimited(limited.RetryAfter),
        { Error: { } error } => new AiFailure.ProviderError(error.Code, error.Message),
        { Stop: ProviderStop.Refused } => new AiFailure.Refused(reply.RefusalReason ?? "refusal"),
        // A cut answer is never parsed in part.
        { Stop: ProviderStop.Truncated } => new AiFailure.InvalidOutput([$"The answer was cut at the output limit of {maxOutputTokens} tokens."]),
        _ => null,
    };

    private static string TaskName(AiTask task) => JsonNamingPolicy.CamelCase.ConvertName(task.ToString());

    private static string SchemaName(string purpose)
    {
        var name = new string(purpose.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(64).ToArray());
        return name.Length == 0 ? "result" : name;
    }
}
