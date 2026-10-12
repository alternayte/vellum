using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Json.Schema;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// Rejects a scenario that is malformed, points at a contract that does not exist, carries a
/// payload its schema does not accept, or exceeds <see cref="TraceLimits"/>. The engine never
/// runs a scenario that has an issue.
/// </summary>
public static class ScenarioValidator
{
    public static IReadOnlyList<ScenarioIssue> Validate(TraceModel model, Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scenario);

        var issues = new List<ScenarioIssue>();
        CheckModel(model, issues);

        if (scenario.Steps is null || scenario.Faults is null || scenario.Assertions is null)
        {
            issues.Add(new("", "malformed-scenario", "A scenario needs steps, faults and assertions lists; an empty list is valid."));
            return issues;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var schemas = new Dictionary<SchemaId, JsonSchema?>();
        var stepCount = 0;
        var schemaClock = new Stopwatch();
        var schemaBudgetSpent = false;

        VisitSequence(scenario.Steps, new HashSet<string>(StringComparer.Ordinal), 1, "");

        if (stepCount > TraceLimits.MaxSteps)
            issues.Add(new("", "too-many-steps", $"A scenario has at most {TraceLimits.MaxSteps} steps; this one has {stepCount}."));

        CheckFaults(model, scenario, issues);
        CheckAssertions(model, scenario, seen, issues);
        return issues;

        // `completed` holds the steps whose outcome is final when the next step of this
        // sequence starts. A branch may read only those.
        void VisitSequence(IReadOnlyList<Step>? steps, HashSet<string> inherited, int depth, string owner)
        {
            if (steps is null)
            {
                issues.Add(new(owner, "malformed-scenario", "A step list is missing; use an empty list for no steps."));
                return;
            }
            if (depth > TraceLimits.MaxDepth)
            {
                issues.Add(new(owner, "too-deep", $"Steps nest at most {TraceLimits.MaxDepth} levels."));
                return;
            }

            // A list counts toward the step limit even when empty: each one is scheduled work.
            stepCount++;
            var completed = new HashSet<string>(inherited, StringComparer.Ordinal);
            foreach (var step in steps)
            {
                if (step is null)
                {
                    issues.Add(new(owner, "malformed-scenario", "A step list holds an empty entry."));
                    continue;
                }
                Visit(step, completed, depth);
                AddCompleted(step, completed, depth);
            }
        }

        void Visit(Step step, HashSet<string> completed, int depth)
        {
            stepCount++;
            if (string.IsNullOrWhiteSpace(step.Id))
                issues.Add(new("", "missing-step-id", "Every step needs an ID."));
            else if (!seen.Add(step.Id))
                issues.Add(new(step.Id, "duplicate-step-id", $"Step ID '{step.Id}' is used more than once."));

            switch (step)
            {
                case CallStep call:
                    RequireElement(step, call.From, "caller");
                    if (model.Operations.TryGetValue(call.Operation, out var operation))
                    {
                        CheckPayload(step, "request", call.Request, operation.RequestSchemaId);
                        CheckPayload(step, "response", call.Response, operation.ResponseSchemaId);
                    }
                    else
                    {
                        issues.Add(new(step.Id, "unknown-operation", $"Operation {call.Operation} does not exist."));
                        CheckPayload(step, "request", call.Request, null);
                        CheckPayload(step, "response", call.Response, null);
                    }
                    VisitSequence(call.Then, completed, depth + 1, step.Id);
                    VisitSequence(call.OnError, completed, depth + 1, step.Id);
                    break;

                case PublishStep publish:
                    RequireElement(step, publish.From, "publisher");
                    if (!model.Channels.ContainsKey(publish.Channel))
                        issues.Add(new(step.Id, "unknown-channel", $"Channel {publish.Channel} does not exist."));
                    if (model.Messages.TryGetValue(publish.Message, out var message))
                        CheckPayload(step, "payload", publish.Payload, message.SchemaId);
                    else
                    {
                        issues.Add(new(step.Id, "unknown-message", $"Message {publish.Message} does not exist."));
                        CheckPayload(step, "payload", publish.Payload, null);
                    }

                    if (publish.Handlers is null || publish.Headers is null)
                    {
                        issues.Add(new(step.Id, "malformed-scenario", "A publish step needs headers and handlers; empty maps are valid."));
                        break;
                    }
                    foreach (var (subscriptionId, handler) in publish.Handlers)
                    {
                        if (!model.Subscriptions.TryGetValue(subscriptionId, out var subscription))
                            issues.Add(new(step.Id, "unknown-subscription", $"Subscription {subscriptionId} does not exist."));
                        else if (subscription.ChannelId != publish.Channel)
                            issues.Add(new(step.Id, "subscription-not-on-channel", $"Subscription {subscriptionId} does not listen to channel {publish.Channel}."));
                        VisitSequence(handler, completed, depth + 1, step.Id);
                    }
                    break;

                case WriteStep write:
                    RequireElement(step, write.By, "writer");
                    RequireElement(step, write.Store, "data store");
                    if (string.IsNullOrWhiteSpace(write.Dataset))
                        issues.Add(new(step.Id, "missing-dataset", "A write names the dataset it changes."));
                    CheckPayload(step, "change", write.Change, null);
                    break;

                case ParallelStep parallel:
                    if (parallel.Branches is null)
                    {
                        issues.Add(new(step.Id, "malformed-scenario", "A parallel step needs a branches list."));
                        break;
                    }
                    foreach (var branch in parallel.Branches)
                        VisitSequence(branch, completed, depth + 1, step.Id);
                    break;

                case BranchStep branch:
                    if (!Enum.IsDefined(branch.Is))
                        issues.Add(new(step.Id, "invalid-outcome", "A branch compares against succeeded, failed or skipped."));
                    if (branch.WhenStep is null || !completed.Contains(branch.WhenStep))
                        issues.Add(new(step.Id, "unknown-step", $"Branch condition names step '{branch.WhenStep}', whose outcome is not final when the branch runs. Name a step that finishes earlier in the same flow."));
                    VisitSequence(branch.Then, completed, depth + 1, step.Id);
                    VisitSequence(branch.Else, completed, depth + 1, step.Id);
                    break;
            }
        }

        // Consumers run after the publisher has moved on, so handler steps never count as
        // completed for whatever follows the publish.
        void AddCompleted(Step step, HashSet<string> completed, int depth)
        {
            if (depth > TraceLimits.MaxDepth || step is null)
                return;
            if (!string.IsNullOrWhiteSpace(step.Id))
                completed.Add(step.Id);

            IEnumerable<IReadOnlyList<Step>?> nested = step switch
            {
                CallStep call => [call.Then, call.OnError],
                ParallelStep parallel => parallel.Branches ?? [],
                BranchStep branch => [branch.Then, branch.Else],
                _ => [],
            };
            foreach (var list in nested)
                foreach (var child in list ?? [])
                    AddCompleted(child, completed, depth + 1);
        }

        void RequireElement(Step step, ElementId id, string role)
        {
            if (!model.Elements.ContainsKey(id))
                issues.Add(new(step.Id, "unknown-element", $"The {role} {id} does not exist."));
        }

        void CheckPayload(Step step, string what, JsonElement? payload, SchemaId? schemaId)
        {
            if (payload is { } given && !IsUsable(step, what, given))
                return;
            if (schemaId is not { } id)
                return;

            if (!schemas.TryGetValue(id, out var schema))
            {
                if (!model.Schemas.TryGetValue(id, out var text))
                    issues.Add(new(step.Id, "unknown-schema", $"Schema {id} does not exist."));
                else if ((schema = Build(text)) is null)
                    issues.Add(new(step.Id, "invalid-schema", $"Schema {id} is not a JSON Schema this version can evaluate."));
                schemas[id] = schema;
            }

            if (schema is null)
                return;

            if (payload is not { } value)
            {
                issues.Add(new(step.Id, "missing-payload", $"The {what} has a schema but the step gives no value."));
                return;
            }

            // A stored schema can hold a pattern that backtracks for seconds per value. The
            // allowance bounds the total; past it the scenario is rejected, never half-checked.
            if (schemaClock.ElapsedMilliseconds > TraceLimits.SchemaBudgetMs)
            {
                if (!schemaBudgetSpent)
                    issues.Add(new(step.Id, "validation-too-slow", "Schema checks took too long. Simplify the patterns in the schemas this scenario uses."));
                schemaBudgetSpent = true;
                return;
            }

            schemaClock.Start();
            try
            {
                var result = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (result.IsValid)
                    return;

                var reasons = (result.Details ?? [])
                    .Where(d => d.Errors is { Count: > 0 })
                    .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                    .Order(StringComparer.Ordinal)
                    .Take(3);
                issues.Add(new(step.Id, "schema-mismatch", $"The {what} does not match its schema. {string.Join("; ", reasons)}"));
            }
            // Schema text is stored input. A reference that cannot resolve or a pattern that
            // cannot compile or finish is a schema defect, never a crash.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                issues.Add(new(step.Id, "invalid-schema", $"Schema {id} cannot be evaluated: {ex.GetType().Name}."));
            }
            finally
            {
                schemaClock.Stop();
            }
        }

        // Every payload is later copied into events and hashed, so it must be bounded and
        // must be JSON that can be written back out.
        bool IsUsable(Step step, string what, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                issues.Add(new(step.Id, "invalid-payload", $"The {what} is not a JSON value."));
                return false;
            }
            if (JsonMarshal.GetRawUtf8Value(value).Length > TraceLimits.MaxPayloadBytes)
            {
                issues.Add(new(step.Id, "payload-too-large", $"The {what} is larger than {TraceLimits.MaxPayloadBytes / 1024} KB. Use a representative fixture."));
                return false;
            }
            try
            {
                using var writer = new Utf8JsonWriter(Stream.Null);
                CanonicalJson.Write(writer, value);
                return true;
            }
            catch (InvalidOperationException)
            {
                issues.Add(new(step.Id, "invalid-payload", $"The {what} holds text that is not valid Unicode."));
                return false;
            }
        }
    }

    /// <summary>
    /// Each schema gets its own registry. The library default is one process-wide registry
    /// keyed by <c>$id</c>, which would make a second run, or another project with the same
    /// <c>$id</c>, fail.
    /// </summary>
    private static JsonSchema? Build(string text)
    {
        try
        {
            return JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void CheckModel(TraceModel model, List<ScenarioIssue> issues)
    {
        void Bad(string message) => issues.Add(new("", "invalid-model", message));

        if (model.Elements is null || model.Operations is null || model.Channels is null
            || model.Subscriptions is null || model.Messages is null || model.Schemas is null
            || model.Operations.Values.Any(o => o is null) || model.Channels.Values.Any(c => c is null)
            || model.Subscriptions.Values.Any(x => x is null) || model.Messages.Values.Any(m => m is null))
        {
            throw new ArgumentException("A trace model has every collection and no empty entries.", nameof(model));
        }

        foreach (var (id, operation) in model.Operations)
        {
            if (operation.Id != id) Bad($"Operation {id} is stored under another ID.");
            if (!model.Elements.ContainsKey(operation.OwnerId)) Bad($"Operation '{operation.Name}' belongs to an element that does not exist.");
            if (operation.TimeoutMs is < 0 or > TraceLimits.MaxDurationMs) Bad($"Operation '{operation.Name}' needs a timeout between 0 and {TraceLimits.MaxDurationMs} ms.");
            CheckRetry(operation.Retry, $"Operation '{operation.Name}'");
        }

        foreach (var (id, channel) in model.Channels)
            if (channel.Id != id) Bad($"Channel {id} is stored under another ID.");

        foreach (var (id, subscription) in model.Subscriptions)
        {
            if (subscription.Id != id) Bad($"Subscription {id} is stored under another ID.");
            if (!model.Channels.ContainsKey(subscription.ChannelId)) Bad($"Subscription {id} listens to a channel that does not exist.");
            if (!model.Elements.ContainsKey(subscription.ConsumerId)) Bad($"Subscription {id} has a consumer that does not exist.");
            if (subscription.DeadLetterChannelId is { } dlq && !model.Channels.ContainsKey(dlq)) Bad($"Subscription {id} dead-letters to a channel that does not exist.");
            CheckRetry(subscription.Retry, $"Subscription {id}");
        }

        foreach (var (id, message) in model.Messages)
            if (message.Id != id) Bad($"Message {id} is stored under another ID.");

        void CheckRetry(RetryPolicy? retry, string owner)
        {
            if (retry is null)
                Bad($"{owner} has no retry policy.");
            else if (retry.MaxAttempts is < 1 or > TraceLimits.MaxAttempts)
                Bad($"{owner} allows between 1 and {TraceLimits.MaxAttempts} attempts.");
            else if (retry.BackoffMs is < 0 or > TraceLimits.MaxDurationMs)
                Bad($"{owner} needs a backoff between 0 and {TraceLimits.MaxDurationMs} ms.");
        }
    }

    private static void CheckFaults(TraceModel model, Scenario scenario, List<ScenarioIssue> issues)
    {
        var targets = new HashSet<Guid>();
        foreach (var fault in scenario.Faults)
        {
            if (fault is null)
            {
                issues.Add(new("", "malformed-scenario", "The faults list holds an empty entry."));
                continue;
            }

            var isOperation = model.Operations.ContainsKey(fault.Target);
            var known = isOperation
                || model.Channels.ContainsKey(fault.Target)
                || model.Subscriptions.ContainsKey(fault.Target)
                || model.Elements.ContainsKey(fault.Target);
            if (!known)
                issues.Add(new("", "unknown-fault-target", $"Fault target {fault.Target} is not an operation, channel, subscription or element."));
            if (!targets.Add(fault.Target))
                issues.Add(new("", "duplicate-fault", $"Target {fault.Target} has more than one fault; give it one."));
            if (!Enum.IsDefined(fault.Kind))
                issues.Add(new("", "invalid-fault", $"Fault on {fault.Target} has an unknown kind."));
            else if (fault.Kind == FaultKind.Timeout && known && !isOperation)
                issues.Add(new("", "invalid-fault", $"Only an operation can time out; use error or outage for {fault.Target}."));
            else if (fault.Kind == FaultKind.Timeout && isOperation && model.Operations[fault.Target].TimeoutMs == 0)
                issues.Add(new("", "invalid-fault", $"Operation {fault.Target} declares no timeout, so it cannot time out. Give it one, or use error."));
            if (fault.FailFirst is < 1)
                issues.Add(new("", "invalid-fault", $"Fault on {fault.Target} must fail at least one attempt."));
        }
    }

    private static void CheckAssertions(TraceModel model, Scenario scenario, HashSet<string> steps, List<ScenarioIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assertion in scenario.Assertions)
        {
            if (assertion is null)
            {
                issues.Add(new("", "malformed-scenario", "The assertions list holds an empty entry."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(assertion.Id) || !ids.Add(assertion.Id))
                issues.Add(new("", "invalid-assertion", $"Assertion ID '{assertion.Id}' is missing or used more than once."));

            switch (assertion)
            {
                case OutcomeAssertion outcome:
                    if (outcome.Step is null || !steps.Contains(outcome.Step))
                        issues.Add(new(outcome.Step ?? "", "unknown-step", $"Assertion {assertion.Id} names step '{outcome.Step}', which is not in the scenario."));
                    if (!Enum.IsDefined(outcome.Expected))
                        issues.Add(new("", "invalid-assertion", $"Assertion {assertion.Id} expects an unknown outcome."));
                    break;

                case EventCountAssertion count:
                    if (count.Expected < 0)
                        issues.Add(new("", "invalid-assertion", $"Assertion {assertion.Id} expects a negative count."));
                    if (!Enum.IsDefined(count.Kind))
                        issues.Add(new("", "invalid-assertion", $"Assertion {assertion.Id} counts an unknown event kind."));
                    if (count.Target is { } target
                        && !model.Operations.ContainsKey(target) && !model.Channels.ContainsKey(target)
                        && !model.Subscriptions.ContainsKey(target) && !model.Elements.ContainsKey(target))
                        issues.Add(new("", "unknown-assertion-target", $"Assertion {assertion.Id} counts events for {target}, which does not exist."));
                    break;
            }
        }
    }
}
