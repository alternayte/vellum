using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.Ai.Usage;
using Vellum.Modules.Settings.Document;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared.Outbound;
using Vellum.Shared.Telemetry;

namespace Vellum.Modules.Ai;

/// <param name="SupportsStructuredOutput">
/// True when the provider's own structured-output mode gave a valid answer, false when the
/// provider rejected that mode or gave an answer outside the schema, null when the test could
/// not tell.
/// </param>
/// <param name="StructuredOutputNote">
/// Set when the provider's own structured-output mode cannot be used: a sentence that says
/// what Vellum does instead and which setting to change.
/// </param>
/// <param name="Error">
/// A stable code and a sentence that says what to change. The sentence holds text that the
/// provider's server wrote only when that server is on the public internet: a base URL on a
/// private network can point at any internal service, and its answer is not shown.
/// </param>
public sealed record AiConnectionTest(
    bool Ok, long LatencyMs, string Model, string? ReportedModel, bool? SupportsStructuredOutput, ConnectionTestError? Error,
    string? StructuredOutputNote = null);

/// <summary>
/// The provider-neutral entry to every AI call. It routes the task to the provider in the
/// workspace's settings, applies that provider's settings, budgets and rate limit, meters
/// each provider call, and returns a typed result. With no provider or no key configured it
/// returns <see cref="AiFailure.NotConfigured"/>; it never throws for a provider fault.
/// Model output is untrusted: only an answer that is valid against the request's schema
/// becomes a value.
/// <para>
/// <b>Private code.</b> A request with a part of class
/// <see cref="AiDataClass.PrivateRepository"/> is sent only when the workspace's effective
/// <c>privacy.sendPrivateCodeToAi</c> is true. The effective value already holds the instance
/// policy. Otherwise the request fails with <see cref="AiFailure.Disabled"/> that names the
/// setting, before a provider is chosen and before anything is metered or sent. This check,
/// and not the secret redactor, is what keeps private code inside the instance.
/// </para>
/// </summary>
public sealed class AiClient(
    IServiceProvider services, SettingsResolver settings, SecretReader secrets, AiUsageLedger ledger, ILogger<AiClient> logger)
{
    /// <summary>The corrected answers a provider is asked for after an invalid one.</summary>
    public const int MaxRepairs = 2;
    public const string TestPurpose = "test-connection";
    /// <summary>The provider error code of a request that the provider rejected for its content, the schema included.</summary>
    private const string RejectedRequest = "invalid_request";

    public const string StructuredOutputRejected =
        "The provider rejected a request in its structured-output mode. Vellum then sends the schema in the prompt and checks the answer itself " +
        "(usage note 'schema_rejected_fallback'), which costs one rejected call for each request. " +
        "Switch off 'supports structured output' for this provider entry to skip that call.";

    public const string StructuredOutputInvalid =
        "The provider accepted its structured-output mode and gave an answer outside the schema. " +
        "Switch off 'supports structured output' for this provider entry; Vellum then sends the schema in the prompt and checks the answer itself.";

    /// <summary>The schema of the connection test. It is one of the schemas that <c>DesignSchemaTests</c> holds to the vendors' limits.</summary>
    internal static readonly JsonElement ConnectionTestSchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
        ["required"] = new JsonArray("ok"),
        ["additionalProperties"] = false,
    });
    private const int MaxReportedModelChars = 200;

    private sealed record Route(AiProviderSettings Provider, IAiProviderAdapter Adapter, ProviderTarget Target, decimal? WorkspaceBudgetUsd);

    /// <summary>The provider calls of one request, added up. A sum that leaves the range of its type becomes unknown.</summary>
    private sealed class Totals(Route route)
    {
        private int _calls;
        private int? _input = 0, _output = 0;
        private decimal? _cost = 0m;
        private string? _reportedModel;

        public void Add(CallCost cost, string? reportedModel)
        {
            _calls++;
            _input = Sum(_input, cost.InputTokens);
            _output = Sum(_output, cost.OutputTokens);
            try
            {
                _cost = _cost is null || cost.CostUsd is null ? null : checked(_cost + cost.CostUsd);
            }
            catch (OverflowException)
            {
                _cost = null;
            }

            _reportedModel = reportedModel ?? _reportedModel;
        }

        private static int? Sum(int? total, int? count)
        {
            if (total is null || count is null)
                return null;
            var sum = (long)total.Value + count.Value;
            return sum is >= 0 and <= int.MaxValue ? (int)sum : null;
        }

        public string? ReportedModel => _reportedModel;

        public AiUsage ToUsage() => new(
            route.Provider.Id, route.Provider.Kind, route.Provider.Model, _reportedModel, _calls, _input, _output, _cost);
    }

    /// <summary>
    /// Asks for one JSON value and returns it as <typeparamref name="T"/> when it is valid
    /// against <see cref="AiRequest.JsonSchema"/>. An invalid answer is sent back for
    /// correction at most <see cref="MaxRepairs"/> times.
    /// <para>
    /// <b>A rejected schema.</b> The schemas of this application stay inside what each vendor
    /// documents, and a vendor can still reject one: a limit changes, or an OpenAI-compatible
    /// server implements less. When a provider that is marked <c>supportsStructuredOutput</c>
    /// rejects the request (the adapters name every request that a provider rejects for its
    /// content <c>invalid_request</c>; no vendor gives a narrower word for a schema), the
    /// request is sent once more with the schema in the prompt, the mode that a provider
    /// without structured output gets. The rejected call costs nothing and is metered as a
    /// provider error; every call after it carries the usage note
    /// <see cref="AiUsageLedger.NoteSchemaFallback"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The request has no schema, or the schema is not a JSON Schema.</exception>
    public async Task<AiResult<T>> CompleteStructuredAsync<T>(AiRequest request, CancellationToken ct = default)
    {
        if (request.JsonSchema is not { } schemaJson || StructuredOutput.Compile(schemaJson) is not { } schema)
            throw new ArgumentException("A structured request needs a JSON Schema.", nameof(request));

        var (route, unroutable) = await RouteAsync(request, ct);
        if (route is null)
            return new AiResult<T>.Failed(unroutable!);

        var native = route.Provider.SupportsStructuredOutput;
        var prompt = PromptAssembler.Build(request, askForJsonInPrompt: !native);
        var totals = new Totals(route);
        var messages = prompt.Messages;
        IReadOnlyList<string> issues = [];
        string? note = null;

        for (var attempt = 0; attempt <= MaxRepairs; attempt++)
        {
            T? value = default;
            var (reply, failure) = await CallAsync(route, request, TaskName(request.Task), prompt, messages, native, totals, answer =>
            {
                if (Failure(answer, request.MaxOutputTokens) is not null)
                    return Outcome(answer);
                issues = schema.Check(answer.Text, lenient: !native, out value);
                return issues.Count == 0 ? "succeeded" : "invalid_output";
            }, ct, note);
            if (reply is null)
                return new AiResult<T>.Failed(failure!);

            // Once: the second mode has no schema that a provider could reject.
            if (native && reply.Error is { Code: RejectedRequest })
            {
                native = false;
                note = AiUsageLedger.NoteSchemaFallback;
                prompt = PromptAssembler.Build(request, askForJsonInPrompt: true);
                messages = prompt.Messages;
                attempt--;
                continue;
            }

            if (Failure(reply, request.MaxOutputTokens) is { } stopped)
                return new AiResult<T>.Failed(stopped);
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
    /// <param name="completed">Receives what the call used, once, after a complete answer and before the stream ends.</param>
    public async IAsyncEnumerable<string> StreamTextAsync(
        AiRequest request, [EnumeratorCancellation] CancellationToken ct = default, Action<AiUsage>? completed = null)
    {
        var (route, unroutable) = await RouteAsync(request, ct);
        if (route is null)
            throw new AiStreamFailedException(unroutable!);

        var prompt = PromptAssembler.Build(request, askForJsonInPrompt: false);
        var totals = new Totals(route);

        if (!route.Provider.SupportsStreaming)
        {
            var (whole, refused) = await CallAsync(route, request, TaskName(request.Task), prompt, prompt.Messages, native: false, totals, Outcome, ct);
            if (whole is null)
                throw new AiStreamFailedException(refused!);
            if (Failure(whole, request.MaxOutputTokens) is { } stopped)
                throw new AiStreamFailedException(stopped);

            yield return whole.Text;
            completed?.Invoke(totals.ToUsage());
            yield break;
        }

        var (ticket, admission) = await ledger.BeginAsync(Start(route, request, TaskName(request.Task), prompt, prompt.Messages, null), ct);
        if (ticket is null)
            throw new AiStreamFailedException(admission!);

        var progress = new CallProgress();
        var providerRequest = new ProviderRequest(
            route.Target, prompt.System, prompt.Messages, null, SchemaName(request.Purpose), request.MaxOutputTokens, progress);
        ProviderReply? end = null;
        try
        {
            await foreach (var item in route.Adapter.StreamAsync(providerRequest, ct))
            {
                if (item is ProviderStreamEvent.Delta delta)
                {
                    progress.OutputStarted = true;
                    yield return delta.Text;
                }
                else if (item is ProviderStreamEvent.End last)
                {
                    end = Cleaned(last.Reply);
                }
            }
        }
        finally
        {
            // Every way out of the stream ends the call's reservation: a complete answer, a
            // provider failure, a cancelled token, a reader that stopped, and an exception.
            // Without a terminal event the caller cancelled or stopped reading.
            await CompleteAsync(route, request, ticket, end, end is null ? "cancelled" : Outcome(end), progress, totals);
        }

        if (end is null)
            throw new AiStreamFailedException(new AiFailure.ProviderError("stream_interrupted", ProviderErrors.Sentence("stream_interrupted")));
        if (Failure(end, request.MaxOutputTokens) is { } failure)
            throw new AiStreamFailedException(failure);
        completed?.Invoke(totals.ToUsage());
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
        if (workspace.Effective.Ai.Providers.FirstOrDefault(p => p.Id == providerId) is not { } provider)
            return null;

        AiConnectionTest Fail(string code, string message, long latency = 0, string? reported = null) =>
            new(false, latency, provider.Model, reported, null, new ConnectionTestError(code, message));

        if (!workspace.Policy.AllowedAiProviderKinds.Contains(provider.Kind))
            return Fail("disabled_by_policy", $"The instance policy does not allow the provider kind '{provider.Kind}'.");

        var (route, unroutable) = await TargetAsync(workspaceId, provider, workspace, ct);
        if (route is null)
        {
            return unroutable is AiFailure.ProviderError error
                ? Fail(error.Code, error.Message)
                : Fail("not_configured", "The provider needs a model name, a base URL where the kind has none of its own, and an API key in the workspace secrets.");
        }

        var schemaJson = ConnectionTestSchema;
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
            var valid = false;
            var (reply, failure) = await CallAsync(route, request, "test", prompt, prompt.Messages, native, totals, answer =>
            {
                if (answer.Error is not null || answer.Stop != ProviderStop.Completed)
                    return Outcome(answer);
                valid = schema.Check<JsonElement>(answer.Text, lenient: !native, out _).Count == 0;
                return valid ? "succeeded" : "invalid_output";
            }, ct);
            if (reply is null)
                return Refused(failure!);

            if (reply.Error is { Code: RejectedRequest } && native)
            {
                structured = false;
                continue;
            }

            if (reply.Error is { } error)
                return Fail(error.Code, error.Message, Elapsed(), totals.ReportedModel);

            // The provider answered. A refusal or a cut answer still proves the connection.
            if (native && reply.Stop == ProviderStop.Completed)
                structured = valid;
            return new AiConnectionTest(true, Elapsed(), provider.Model, totals.ReportedModel, structured, null,
                structured != false ? null : native ? StructuredOutputInvalid : StructuredOutputRejected);
        }

        // Unreachable: the plain-text pass returns in every branch.
        throw new UnreachableException();
    }

    private async Task<(Route? Route, AiFailure? Failure)> RouteAsync(AiRequest request, CancellationToken ct)
    {
        var workspace = await settings.GetWorkspaceAsync(request.WorkspaceId, ct);

        // The gate for private code comes first: no provider is chosen, no key is read and
        // nothing is metered for a request that may not leave the instance.
        if (!workspace.Effective.Privacy.SendPrivateCodeToAi && request.Messages.Any(message => message.Parts.Any(part => part.IsPrivateCode)))
            return (null, new AiFailure.Disabled(AiFailure.Disabled.PrivateCodeSetting));

        var routing = workspace.Effective.Ai.TaskRouting;
        var providerId = request.Task switch
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
            return (null, StoredRoute(workspace, request.Task) is null ? new AiFailure.NotConfigured() : new AiFailure.Disabled());
        }

        if (workspace.Effective.Ai.Providers.FirstOrDefault(p => p.Id == providerId) is not { } provider)
            return (null, new AiFailure.NotConfigured());
        if (!provider.Enabled)
            return (null, new AiFailure.Disabled());

        return await TargetAsync(request.WorkspaceId, provider, workspace, ct);
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
        Guid workspaceId, AiProviderSettings provider, WorkspaceSettings workspace, CancellationToken ct)
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

        // The policy and the https rule come from the same settings read as the URL. The
        // pipeline applies both to every attempt.
        var target = new ProviderTarget(
            provider.Id, provider.Kind, provider.Model, baseUrl, apiKey is null ? null : adapter.Credential(apiKey),
            workspace.Policy.AllowPrivateNetworkUrls, workspace.RequireHttps, TimeSpan.FromSeconds(provider.TimeoutSeconds), provider.Temperature);
        return (new Route(provider, adapter, target, workspace.Effective.Usage.MonthlyAiBudgetUsd), null);
    }

    private static CallStart Start(
        Route route, AiRequest request, string task, AssembledPrompt prompt, IReadOnlyList<ProviderMessage> messages, JsonElement? schema) =>
        new(request.WorkspaceId, route.Provider.Id, route.Provider.Kind, route.Provider.Model, task, request.Purpose,
            request.PromptTemplateVersion, request.ActorId, route.Provider.MonthlyBudgetUsd, route.WorkspaceBudgetUsd,
            route.Provider.RequestsPerMinute, prompt.ByteCount(messages, schema), route.Adapter.OutputTokenCeiling(request.MaxOutputTokens));

    /// <summary>
    /// Admits, makes and meters one provider call. The call's reservation ends on every way
    /// out: an answer, a provider failure, a cancelled token and an exception.
    /// </summary>
    /// <param name="outcomeOf">Names the outcome of an answer for the usage record. It can check the answer against a schema.</param>
    /// <returns>A null reply means that no call was made or that it was cancelled.</returns>
    private async Task<(ProviderReply? Reply, AiFailure? Failure)> CallAsync(
        Route route, AiRequest request, string task, AssembledPrompt prompt, IReadOnlyList<ProviderMessage> messages, bool native,
        Totals totals, Func<ProviderReply, string> outcomeOf, CancellationToken ct, string? note = null)
    {
        var schema = native ? request.JsonSchema : null;
        CallTicket? ticket;
        try
        {
            (ticket, var refused) = await ledger.BeginAsync(Start(route, request, task, prompt, messages, schema), ct);
            if (ticket is null)
                return (null, refused);
        }
        catch (OperationCanceledException)
        {
            return (null, new AiFailure.Cancelled());
        }

        var progress = new CallProgress();
        ProviderReply? reply = null;
        // An exception inside Vellum leaves this word. Every other path replaces it.
        var outcome = "failed";
        try
        {
            reply = Cleaned(await route.Adapter.CompleteAsync(
                new ProviderRequest(route.Target, prompt.System, messages, schema, SchemaName(request.Purpose), request.MaxOutputTokens, progress), ct));
            outcome = outcomeOf(reply);
            return (reply, null);
        }
        catch (OperationCanceledException)
        {
            reply = null;
            outcome = "cancelled";
            return (null, new AiFailure.Cancelled());
        }
        finally
        {
            await CompleteAsync(route, request, ticket, reply, outcome, progress, totals, note);
        }
    }

    private async Task CompleteAsync(
        Route route, AiRequest request, CallTicket ticket, ProviderReply? reply, string outcome, CallProgress progress, Totals totals,
        string? note = null)
    {
        var errorCode = reply?.Error?.Code;
        var cost = await ledger.CompleteAsync(ticket, new CallEnd(
            outcome, errorCode, reply?.InputTokens, reply?.OutputTokens, reply?.ReportedModel, progress.MayHaveGenerated(reply?.Error), note));
        if (reply is not null)
            totals.Add(cost, reply.ReportedModel);

        VellumMetrics.AiCallEnded(route.Provider.Kind, outcome, reply?.InputTokens, reply?.OutputTokens);

        // Labels and counts only. No prompt text, no answer text and no credential is logged.
        logger.Log(outcome == "succeeded" ? LogLevel.Information : LogLevel.Warning,
            "AI call {ProviderKind} {Task} {Purpose} ended {Outcome} {ErrorCode}",
            route.Provider.Kind, request.Task, request.Purpose, outcome, errorCode ?? "-");
    }

    /// <summary>The model name that the provider reported is remote text: it is bounded and holds no control character.</summary>
    private static ProviderReply Cleaned(ProviderReply reply) =>
        reply.ReportedModel is { } model ? reply with { ReportedModel = OutboundText.Clean(model, MaxReportedModelChars) } : reply;

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
