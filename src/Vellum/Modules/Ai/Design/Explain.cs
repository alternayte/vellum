using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Ai.Design;

/// <param name="Kind"><c>element</c>, <c>operation</c>, <c>channel</c>, <c>subscription</c>, <c>message</c>, <c>scenario</c> or <c>trace-event</c>.</param>
/// <param name="Id">The ID of the thing. For <c>trace-event</c> it is not used.</param>
/// <param name="ScenarioId">For <c>trace-event</c>: the scenario whose playback holds the event.</param>
/// <param name="Sequence">For <c>trace-event</c>: the sequence number of the event in that playback.</param>
public sealed record ExplainTarget(string? Kind, Guid? Id, Guid? ScenarioId, int? Sequence);

/// <param name="Question">At most 2,000 characters. Without one, the part is explained in general.</param>
/// <param name="BranchId">The draft or variant to read. Null means the main model.</param>
public sealed record ExplainRequest(ExplainTarget? Target, string? Question, Guid? BranchId);

/// <summary>
/// Rubber Duck explanations. The answer is plain text that is streamed to the reader. It is
/// never parsed, never applied and never stored; only the usage row of the call remains. The
/// prompt holds the target and its direct neighbours as data.
/// <para>
/// The response is <c>text/event-stream</c>. When private-sourced things were left out of the
/// context, the first event is <c>data: {"notice": {"omittedPrivate": n}}</c>. Then
/// <c>data: {"delta": "..."}</c> for each piece of text, then
/// <c>data: {"done": true, "usage": {...}, "disclaimer": "declared-design"}</c>, or
/// <c>data: {"error": {"code", "message"}}</c> when the provider fails after text was sent.
/// The disclaimer is a fixed word and not text of the model: the client shows the limitation
/// line for it whatever the model wrote. When no text can be produced at all, the response is
/// JSON <see cref="AiUnavailable"/> with status 200. Both forms carry
/// <c>Cache-Control: no-store</c>.
/// </para>
/// <para>
/// A target that is private-sourced is not explained while
/// <c>privacy.sendPrivateCodeToAi</c> is off: the answer is the unavailable state that names
/// the setting, and no provider is called.
/// </para>
/// </summary>
public static class Explain
{
    public const string Purpose = "explain";
    public const int MaxQuestionChars = 2_000;
    public const int MaxOutputTokens = 1_500;
    /// <summary>The most characters of context one explanation sends. A larger context is cut and says so.</summary>
    public const int MaxContextChars = 60_000;
    /// <summary>The word of the <c>done</c> event that tells the client to show the limitation line (SDD guarantee 8).</summary>
    public const string Disclaimer = "declared-design";
    private const int MaxNeighbours = 50;
    private const string Withheld = "(withheld)";

    private static readonly string[] Kinds = [.. AliasKinds.Contracts, "scenario", "trace-event"];

    public static async Task<IResult> Handle(
        Guid projectId, ExplainRequest body, HttpContext http,
        AiClient ai, IEventStore store, SchemasDbContext schemasDb, PrivateSources privateSources, ExplainStreams streams,
        CancellationToken ct)
    {
        // An explanation is made for one reader at one moment. No cache keeps it, whatever the answer is.
        http.Response.Headers.CacheControl = "no-store";

        var (caller, refusal) = await DesignAccess.ResolveAsync(http, projectId, body.BranchId, WorkspaceRole.Viewer, forWrite: false, ct);
        if (caller is null)
            return refusal!;

        if (body.Target is not { Kind: { } kind } target || !Kinds.Contains(kind))
            return DesignAccess.Invalid("target.kind", $"Must be one of: {string.Join(", ", Kinds)}");
        if (kind == "trace-event" ? target is not { ScenarioId: not null, Sequence: >= 0 } : target.Id is null)
        {
            return kind == "trace-event"
                ? DesignAccess.Invalid("target.sequence", "A trace event needs 'scenarioId' and a 'sequence' of 0 or more")
                : DesignAccess.Invalid("target.id", "An ID is required");
        }
        if (body.Question is not null && TextRule.Lines("question", body.Question, MaxQuestionChars) is { } questionError)
            return DesignAccess.Invalid(questionError.Field, questionError.Message);

        var snapshot = await BranchSnapshot.LoadAsync(store, schemasDb, projectId, caller.StreamId, ct);
        var gate = await privateSources.LoadAsync(caller.WorkspaceId, projectId, snapshot.Model, ct);
        if (kind is not ("scenario" or "trace-event") && gate.Hides(target.Id!.Value))
            return Results.Ok(AiUnavailable.From(new AiFailure.Disabled(PrivateGate.Setting)));

        var scope = new PrivateScope(gate);
        var (context, problem) = await ContextAsync(store, projectId, snapshot, target, kind, scope, ct);
        if (context is null)
            return problem!;

        var text = context.ToJsonString();
        if (text.Length > MaxContextChars)
            text = text[..MaxContextChars] + "\n(The context is larger than this; the rest was left out.)";

        var prompt = DesignPrompts.Explain;
        var parts = new List<AiPart>
        {
            AiPart.Instruction(prompt["context"]),
            AiPart.Untrusted(text, scope.HoldsPrivate ? AiDataClass.PrivateRepository : AiDataClass.Public),
        };
        if (scope.Omitted > 0)
            parts.Add(AiPart.Instruction(prompt["withheld"]));
        if (string.IsNullOrWhiteSpace(body.Question))
        {
            parts.Add(AiPart.Instruction(prompt["default"]));
        }
        else
        {
            parts.Add(AiPart.Instruction(prompt["question"]));
            parts.Add(AiPart.Untrusted(body.Question, AiDataClass.Public));
        }

        // A stream holds a provider call open for as long as the provider writes. A reader has a few at a time.
        var lease = streams.Acquire(caller.UserId);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return Results.Json(
                new ErrorResponse("too_many_streams",
                    $"You have {ExplainStreams.MaxPerUser} explanations in progress, which is the most at one time.",
                    "Wait for one to end, or close it, then ask again."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        AiUsage? usage = null;
        IAsyncEnumerator<string>? stream = null;
        var handedOver = false;
        try
        {
            stream = ai.StreamTextAsync(
                new AiRequest(caller.WorkspaceId, AiTask.Explain, Purpose, DesignPrompts.ExplainVersion, prompt["system"],
                    [new AiMessage(AiRole.User, parts)], null, MaxOutputTokens, caller.UserId),
                ct, used => usage = used).GetAsyncEnumerator(ct);

            // The first piece is read before the response starts, so a request that gives no
            // text at all is answered as a state in JSON and not as a broken stream.
            bool hasText;
            try
            {
                hasText = await stream.MoveNextAsync();
            }
            catch (AiStreamFailedException failed)
            {
                return Results.Ok(AiUnavailable.From(failed.Failure));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }

            handedOver = true;
            return new ExplainStream(stream, hasText, () => usage, scope.Omitted, lease);
        }
        finally
        {
            // The stream result ends the provider call and gives the place back when it has run.
            if (!handedOver)
            {
                if (stream is not null)
                    await stream.DisposeAsync();
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// Says which things of a context may be named. A private-sourced thing that may not be
    /// sent is left out and counted once; one that may be sent makes the context private.
    /// </summary>
    private sealed class PrivateScope(PrivateGate gate)
    {
        private readonly HashSet<Guid> _omitted = [];

        public int Omitted => _omitted.Count;

        public bool HoldsPrivate { get; private set; }

        /// <summary>True when the thing may be named in the context.</summary>
        public bool Shows(Guid id)
        {
            if (!gate.Ids.Contains(id))
                return true;
            if (gate.MaySend)
            {
                HoldsPrivate = true;
                return true;
            }

            _omitted.Add(id);
            return false;
        }
    }

    private static async Task<(JsonObject? Context, IResult? Problem)> ContextAsync(
        IEventStore store, Guid projectId, BranchSnapshot snapshot, ExplainTarget target, string kind, PrivateScope scope, CancellationToken ct)
    {
        var model = snapshot.Model;
        var labels = TraceModelBuilder.Labels(model);
        // A name of a thing that may not be sent is replaced by one fixed word.
        string Label(Guid id) => !scope.Shows(id) ? Withheld : labels.GetValueOrDefault(id, "(not in the model)");
        JsonNode? Element(Guid? id) => id is { } value && model.Elements.TryGetValue(value, out var element) && scope.Shows(value)
            ? new JsonObject { ["name"] = element.Name, ["kind"] = element.Kind.ToString().ToLowerInvariant() }
            : null;
        // The source pointer names a file of a repository. It is the target's own, and the target is shown.
        JsonObject Fact(FactStatus status, Provenance? provenance) => new()
        {
            ["factStatus"] = status.ToApiString(),
            ["source"] = provenance?.Source,
            ["confidence"] = provenance?.Confidence,
        };
        static JsonArray List(IEnumerable<JsonNode?> items) => new([.. items.Take(MaxNeighbours)]);

        switch (kind)
        {
            case AliasKinds.Element when model.Elements.TryGetValue(target.Id!.Value, out var element):
                scope.Shows(element.Id);
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "element", ["kind"] = element.Kind.ToString().ToLowerInvariant(), ["name"] = element.Name,
                        ["description"] = element.Description, ["technology"] = element.Technology,
                        ["status"] = element.Status.ToString().ToLowerInvariant(), ["truth"] = Fact(element.FactStatus, element.Provenance),
                    },
                    ["parent"] = Element(element.ParentId?.Value),
                    ["children"] = List(model.Elements.Values.Where(e => e.ParentId == element.Id).OrderBy(e => e.Name, StringComparer.Ordinal)
                        .Select(e => Element(e.Id)).Where(e => e is not null)),
                    ["relationships"] = List(model.Relationships.Values
                        .Where(r => r.FromId == element.Id || r.ToId == element.Id).OrderBy(r => r.Id.Value)
                        .Where(r => scope.Shows(r.FromId == element.Id ? r.ToId : r.FromId))
                        .Select(r => (JsonNode?)new JsonObject
                        {
                            ["direction"] = r.FromId == element.Id ? "outgoing" : "incoming",
                            ["other"] = Label(r.FromId == element.Id ? r.ToId : r.FromId),
                            ["label"] = r.Label, ["technology"] = r.Technology, ["kind"] = r.Kind.ToString().ToLowerInvariant(),
                        })),
                    ["operationsItServes"] = List(model.Operations.Values.Where(o => o.OwnerId == element.Id).OrderBy(o => o.Name, StringComparer.Ordinal)
                        .Where(o => scope.Shows(o.Id))
                        .Select(o => (JsonNode?)new JsonObject { ["name"] = o.Name, ["transport"] = o.Transport.ToString().ToLowerInvariant() })),
                    ["subscriptions"] = List(model.Subscriptions.Values.Where(s => s.ConsumerId == element.Id).OrderBy(s => s.Id.Value)
                        .Where(s => scope.Shows(s.Id) && scope.Shows(s.ChannelId))
                        .Select(s => (JsonNode?)new JsonObject { ["channel"] = Label(s.ChannelId), ["maxAttempts"] = s.Retry.MaxAttempts })),
                    ["messagesItProduces"] = List(model.Messages.Values.Where(m => m.ProducerId == element.Id).OrderBy(m => m.Name, StringComparer.Ordinal)
                        .Where(m => scope.Shows(m.Id)).Select(m => (JsonNode?)m.Name)),
                    ["messagesItConsumes"] = List(model.Messages.Values.Where(m => m.ConsumerIds.Contains(element.Id)).OrderBy(m => m.Name, StringComparer.Ordinal)
                        .Where(m => scope.Shows(m.Id)).Select(m => (JsonNode?)m.Name)),
                }, null);

            case AliasKinds.Operation when model.Operations.TryGetValue(target.Id!.Value, out var operation):
                scope.Shows(operation.Id);
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "operation", ["name"] = operation.Name, ["transport"] = operation.Transport.ToString().ToLowerInvariant(),
                        ["timeoutMs"] = operation.TimeoutMs, ["maxAttempts"] = operation.Retry.MaxAttempts, ["backoffMs"] = operation.Retry.BackoffMs,
                        ["hasRequestSchema"] = operation.RequestSchemaId is not null, ["hasResponseSchema"] = operation.ResponseSchemaId is not null,
                        ["truth"] = Fact(operation.FactStatus, operation.Provenance),
                    },
                    ["owner"] = Element(operation.OwnerId),
                }, null);

            case AliasKinds.Channel when model.Channels.TryGetValue(target.Id!.Value, out var channel):
                scope.Shows(channel.Id);
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "channel", ["name"] = channel.Name, ["kind"] = channel.Kind.ToString().ToLowerInvariant(),
                        ["truth"] = Fact(channel.FactStatus, channel.Provenance),
                    },
                    ["subscriptions"] = List(model.Subscriptions.Values.Where(s => s.ChannelId == channel.Id).OrderBy(s => s.Id.Value)
                        .Where(s => scope.Shows(s.Id) && scope.Shows(s.ConsumerId))
                        .Select(s => (JsonNode?)new JsonObject
                        {
                            ["consumer"] = Label(s.ConsumerId), ["maxAttempts"] = s.Retry.MaxAttempts,
                            ["deadLetterChannel"] = s.DeadLetterChannelId is { } dlq && scope.Shows(dlq) ? Label(dlq) : null,
                        })),
                    ["deadLetterChannelFor"] = List(model.Subscriptions.Values.Where(s => s.DeadLetterChannelId == channel.Id).OrderBy(s => s.Id.Value)
                        .Where(s => scope.Shows(s.Id) && scope.Shows(s.ChannelId) && scope.Shows(s.ConsumerId))
                        .Select(s => (JsonNode?)Label(s.Id))),
                }, null);

            case AliasKinds.Subscription when model.Subscriptions.TryGetValue(target.Id!.Value, out var subscription):
                scope.Shows(subscription.Id);
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "subscription", ["channel"] = Label(subscription.ChannelId), ["consumer"] = Label(subscription.ConsumerId),
                        ["maxAttempts"] = subscription.Retry.MaxAttempts, ["backoffMs"] = subscription.Retry.BackoffMs,
                        ["deadLetterChannel"] = subscription.DeadLetterChannelId is { } deadLetter ? Label(deadLetter) : null,
                        ["truth"] = Fact(subscription.FactStatus, subscription.Provenance),
                    },
                    ["consumer"] = Element(subscription.ConsumerId),
                }, null);

            case AliasKinds.Message when model.Messages.TryGetValue(target.Id!.Value, out var message):
                scope.Shows(message.Id);
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "message", ["name"] = message.Name, ["description"] = message.Description,
                        ["hasSchema"] = message.SchemaId is not null, ["truth"] = Fact(message.FactStatus, message.Provenance),
                    },
                    ["producer"] = Element(message.ProducerId),
                    ["consumers"] = List(message.ConsumerIds.Select(c => Element(c)).Where(c => c is not null)),
                }, null);

            case "scenario":
            case "trace-event":
                var scenarioId = kind == "scenario" ? target.Id!.Value : target.ScenarioId!.Value;
                if ((await ScenarioStreams.LoadAsync(store, scenarioId, ct)).DocumentIn(projectId) is not { } scenario)
                    return (null, DesignAccess.NotFound("Scenario"));

                // A subscription's label holds the names of its channel and consumer.
                string Named(Guid id) =>
                    model.Subscriptions.TryGetValue(id, out var named) && (!scope.Shows(named.ChannelId) || !scope.Shows(named.ConsumerId))
                        ? Withheld
                        : Label(id);
                var steps = FlowAssembler.Flatten(scenario, Named);
                if (kind == "scenario")
                {
                    var (faults, assertions) = FlowAssembler.FlattenChecks(scenario, Named);
                    return (new JsonObject
                    {
                        ["target"] = new JsonObject { ["type"] = "scenario", ["name"] = scenario.Name },
                        ["steps"] = JsonSerializer.SerializeToNode(steps, DesignJson.Options),
                        ["faults"] = JsonSerializer.SerializeToNode(faults, DesignJson.Options),
                        ["assertions"] = JsonSerializer.SerializeToNode(assertions, DesignJson.Options),
                    }, null);
                }

                var trace = TraceEngine.Run(TraceModelBuilder.Build(model, snapshot.Schemas), scenario);
                if (!trace.IsValid)
                    return (null, DesignAccess.Conflict("The scenario has validation issues on this branch, so it has no trace to explain."));
                if (trace.Events.FirstOrDefault(e => e.Sequence == target.Sequence) is not { } traced)
                    return (null, DesignAccess.NotFound("Trace event"));

                // A payload of a valid trace passed the validator, so it has a canonical form and parses again.
                JsonObject Event(TraceEvent e, bool withPayload) => new()
                {
                    ["sequence"] = e.Sequence, ["atMs"] = e.AtMs, ["kind"] = e.Kind.ToString(), ["stepId"] = e.StepId, ["attempt"] = e.Attempt,
                    ["from"] = e.From is { } from ? Named(from) : null,
                    ["to"] = e.To is { } to ? Named(to) : null,
                    ["target"] = e.Target is { } about ? Named(about) : null,
                    ["message"] = e.Message is { } carried ? Named(carried) : null,
                    ["detail"] = e.Detail,
                    ["payload"] = withPayload && e.Payload is { } payload ? JsonNode.Parse(payload.GetRawText()) : null,
                };
                var at = trace.Events.ToList().IndexOf(traced);
                return (new JsonObject
                {
                    ["target"] = Event(traced, withPayload: true),
                    ["type"] = "trace-event",
                    ["scenario"] = scenario.Name,
                    ["step"] = steps.FirstOrDefault(s => s.Id == traced.StepId) is { } step ? JsonSerializer.SerializeToNode(step, DesignJson.Options) : null,
                    ["eventsBefore"] = new JsonArray([.. trace.Events.Skip(Math.Max(0, at - 3)).Take(Math.Min(3, at)).Select(e => (JsonNode?)Event(e, withPayload: false))]),
                    ["eventsAfter"] = new JsonArray([.. trace.Events.Skip(at + 1).Take(3).Select(e => (JsonNode?)Event(e, withPayload: false))]),
                    ["outcomeOfTheStep"] = trace.Outcomes.TryGetValue(traced.StepId, out var outcome) ? outcome.ToString().ToLowerInvariant() : null,
                }, null);

            default:
                return (null, DesignAccess.NotFound(char.ToUpperInvariant(kind[0]) + kind[1..]));
        }
    }
}

/// <summary>
/// The explanations that a user has in progress. A stream keeps a provider call and a
/// connection open, so one user has at most <see cref="MaxPerUser"/> at a time. A place is
/// free again when the stream ends, in any way.
/// </summary>
public sealed class ExplainStreams : IDisposable
{
    public const int MaxPerUser = 2;

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        userId => RateLimitPartition.GetConcurrencyLimiter(userId, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = MaxPerUser,
            QueueLimit = 0,
        }));

    public RateLimitLease Acquire(string userId) => _limiter.AttemptAcquire(userId);

    public void Dispose() => _limiter.Dispose();
}

/// <param name="OmittedPrivate">How many private-sourced things were left out of the context.</param>
public sealed record ExplainNoticeBody(int OmittedPrivate);
/// <summary>The first event of a stream whose context left something out.</summary>
public sealed record ExplainNotice(ExplainNoticeBody Notice);
public sealed record ExplainDelta(string Delta);
/// <param name="Disclaimer">Always <c>declared-design</c>: the explanation describes the declared design and is no evidence about a running system.</param>
public sealed record ExplainDone(bool Done, ProposalUsage? Usage, string Disclaimer);
public sealed record ExplainFailed(ExplainError Error);
public sealed record ExplainError(string Code, string Message);

/// <summary>Writes an explanation as server-sent events while the provider produces it.</summary>
internal sealed class ExplainStream(
    IAsyncEnumerator<string> stream, bool hasText, Func<AiUsage?> usage, int omittedPrivate, IDisposable place) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        var response = http.Response;
        var ct = http.RequestAborted;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        // A proxy that buffers would hold the text back until the end.
        response.Headers["X-Accel-Buffering"] = "no";

        async Task Send<T>(T value)
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(value, DesignJson.Options)}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        using (place)
        await using (stream)
        {
            try
            {
                if (omittedPrivate > 0)
                    await Send(new ExplainNotice(new ExplainNoticeBody(omittedPrivate)));
                for (var more = hasText; more; more = await stream.MoveNextAsync())
                    await Send(new ExplainDelta(stream.Current));
                await Send(new ExplainDone(true, usage() is { } used ? ProposalUsage.From(used) : null, Explain.Disclaimer));
            }
            catch (AiStreamFailedException failed)
            {
                var reason = AiUnavailable.From(failed.Failure).Reason;
                await Send(new ExplainFailed(new ExplainError(reason.Code, reason.Message)));
            }
            // The reader left. The stream's disposal ends the provider call and its usage row.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }
}
