using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The response is <c>text/event-stream</c>: <c>data: {"delta": "..."}</c> for each piece of
/// text, then <c>data: {"done": true, "usage": {...}}</c>, or <c>data: {"error": {"code",
/// "message"}}</c> when the provider fails after text was sent. When no text can be produced
/// at all, the response is JSON <see cref="AiUnavailable"/> with status 200.
/// </para>
/// </summary>
public static class Explain
{
    public const string Purpose = "explain";
    public const int MaxQuestionChars = 2_000;
    public const int MaxOutputTokens = 1_500;
    /// <summary>The most characters of context one explanation sends. A larger context is cut and says so.</summary>
    public const int MaxContextChars = 60_000;
    private const int MaxNeighbours = 50;

    private static readonly string[] Kinds = [.. AliasKinds.Contracts, "scenario", "trace-event"];

    public static async Task<IResult> Handle(
        Guid projectId, ExplainRequest body, HttpContext http,
        AiClient ai, IEventStore store, SchemasDbContext schemasDb, CancellationToken ct)
    {
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
        var (context, problem) = await ContextAsync(store, projectId, snapshot, target, kind, ct);
        if (context is null)
            return problem!;

        var text = context.ToJsonString();
        if (text.Length > MaxContextChars)
            text = text[..MaxContextChars] + "\n(The context is larger than this; the rest was left out.)";

        var prompt = DesignPrompts.Explain;
        var parts = new List<AiPart> { AiPart.Instruction(prompt["context"]), AiPart.Untrusted(text, AiDataClass.Public) };
        if (string.IsNullOrWhiteSpace(body.Question))
        {
            parts.Add(AiPart.Instruction(prompt["default"]));
        }
        else
        {
            parts.Add(AiPart.Instruction(prompt["question"]));
            parts.Add(AiPart.Untrusted(body.Question, AiDataClass.Public));
        }

        AiUsage? usage = null;
        var stream = ai.StreamTextAsync(
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
            await stream.DisposeAsync();
            return Results.Ok(AiUnavailable.From(failed.Failure));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await stream.DisposeAsync();
            return Results.Empty;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return new ExplainStream(stream, hasText, () => usage);
    }

    private static async Task<(JsonObject? Context, IResult? Problem)> ContextAsync(
        IEventStore store, Guid projectId, BranchSnapshot snapshot, ExplainTarget target, string kind, CancellationToken ct)
    {
        var model = snapshot.Model;
        var labels = TraceModelBuilder.Labels(model);
        string Label(Guid id) => labels.GetValueOrDefault(id, "(not in the model)");
        JsonNode? Element(Guid? id) => id is { } value && model.Elements.TryGetValue(value, out var element)
            ? new JsonObject { ["name"] = element.Name, ["kind"] = element.Kind.ToString().ToLowerInvariant() }
            : null;
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
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "element", ["kind"] = element.Kind.ToString().ToLowerInvariant(), ["name"] = element.Name,
                        ["description"] = element.Description, ["technology"] = element.Technology,
                        ["status"] = element.Status.ToString().ToLowerInvariant(), ["truth"] = Fact(element.FactStatus, element.Provenance),
                    },
                    ["parent"] = Element(element.ParentId?.Value),
                    ["children"] = List(model.Elements.Values.Where(e => e.ParentId == element.Id).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => Element(e.Id))),
                    ["relationships"] = List(model.Relationships.Values
                        .Where(r => r.FromId == element.Id || r.ToId == element.Id).OrderBy(r => r.Id.Value)
                        .Select(r => (JsonNode?)new JsonObject
                        {
                            ["direction"] = r.FromId == element.Id ? "outgoing" : "incoming",
                            ["other"] = Label(r.FromId == element.Id ? r.ToId : r.FromId),
                            ["label"] = r.Label, ["technology"] = r.Technology, ["kind"] = r.Kind.ToString().ToLowerInvariant(),
                        })),
                    ["operationsItServes"] = List(model.Operations.Values.Where(o => o.OwnerId == element.Id).OrderBy(o => o.Name, StringComparer.Ordinal)
                        .Select(o => (JsonNode?)new JsonObject { ["name"] = o.Name, ["transport"] = o.Transport.ToString().ToLowerInvariant() })),
                    ["subscriptions"] = List(model.Subscriptions.Values.Where(s => s.ConsumerId == element.Id).OrderBy(s => s.Id.Value)
                        .Select(s => (JsonNode?)new JsonObject { ["channel"] = Label(s.ChannelId), ["maxAttempts"] = s.Retry.MaxAttempts })),
                    ["messagesItProduces"] = List(model.Messages.Values.Where(m => m.ProducerId == element.Id).OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (JsonNode?)m.Name)),
                    ["messagesItConsumes"] = List(model.Messages.Values.Where(m => m.ConsumerIds.Contains(element.Id)).OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (JsonNode?)m.Name)),
                }, null);

            case AliasKinds.Operation when model.Operations.TryGetValue(target.Id!.Value, out var operation):
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
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "channel", ["name"] = channel.Name, ["kind"] = channel.Kind.ToString().ToLowerInvariant(),
                        ["truth"] = Fact(channel.FactStatus, channel.Provenance),
                    },
                    ["subscriptions"] = List(model.Subscriptions.Values.Where(s => s.ChannelId == channel.Id).OrderBy(s => s.Id.Value)
                        .Select(s => (JsonNode?)new JsonObject
                        {
                            ["consumer"] = Label(s.ConsumerId), ["maxAttempts"] = s.Retry.MaxAttempts,
                            ["deadLetterChannel"] = s.DeadLetterChannelId is { } dlq ? Label(dlq) : null,
                        })),
                    ["deadLetterChannelFor"] = List(model.Subscriptions.Values.Where(s => s.DeadLetterChannelId == channel.Id).OrderBy(s => s.Id.Value)
                        .Select(s => (JsonNode?)Label(s.Id))),
                }, null);

            case AliasKinds.Subscription when model.Subscriptions.TryGetValue(target.Id!.Value, out var subscription):
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
                return (new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "message", ["name"] = message.Name, ["description"] = message.Description,
                        ["hasSchema"] = message.SchemaId is not null, ["truth"] = Fact(message.FactStatus, message.Provenance),
                    },
                    ["producer"] = Element(message.ProducerId),
                    ["consumers"] = List(message.ConsumerIds.Select(c => Element(c))),
                }, null);

            case "scenario":
            case "trace-event":
                var scenarioId = kind == "scenario" ? target.Id!.Value : target.ScenarioId!.Value;
                if ((await ScenarioStreams.LoadAsync(store, scenarioId, ct)).DocumentIn(projectId) is not { } scenario)
                    return (null, DesignAccess.NotFound("Scenario"));

                var steps = FlowAssembler.Flatten(scenario, Label);
                if (kind == "scenario")
                {
                    var (faults, assertions) = FlowAssembler.FlattenChecks(scenario, Label);
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

                JsonObject Event(TraceEvent e, bool withPayload) => new()
                {
                    ["sequence"] = e.Sequence, ["atMs"] = e.AtMs, ["kind"] = e.Kind.ToString(), ["stepId"] = e.StepId, ["attempt"] = e.Attempt,
                    ["from"] = e.From is { } from ? Label(from) : null,
                    ["to"] = e.To is { } to ? Label(to) : null,
                    ["target"] = e.Target is { } about ? Label(about) : null,
                    ["message"] = e.Message is { } carried ? Label(carried) : null,
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

public sealed record ExplainDelta(string Delta);
public sealed record ExplainDone(bool Done, ProposalUsage? Usage);
public sealed record ExplainFailed(ExplainError Error);
public sealed record ExplainError(string Code, string Message);

/// <summary>Writes an explanation as server-sent events while the provider produces it.</summary>
internal sealed class ExplainStream(IAsyncEnumerator<string> stream, bool hasText, Func<AiUsage?> usage) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        var response = http.Response;
        var ct = http.RequestAborted;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // A proxy that buffers would hold the text back until the end.
        response.Headers["X-Accel-Buffering"] = "no";

        async Task Send<T>(T value)
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(value, DesignJson.Options)}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        await using (stream)
        {
            try
            {
                for (var more = hasText; more; more = await stream.MoveNextAsync())
                    await Send(new ExplainDelta(stream.Current));
                await Send(new ExplainDone(true, usage() is { } used ? ProposalUsage.From(used) : null));
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
