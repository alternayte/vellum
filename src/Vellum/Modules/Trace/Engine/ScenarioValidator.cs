using System.Text.Json;
using Json.Schema;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Trace.Engine;

/// <summary>
/// Rejects a scenario that points at a contract that does not exist or carries a payload
/// its schema does not accept. The engine never runs an invalid scenario.
/// </summary>
public static class ScenarioValidator
{
    public static IReadOnlyList<ScenarioIssue> Validate(TraceModel model, Scenario scenario)
    {
        var issues = new List<ScenarioIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var schemas = new Dictionary<SchemaId, JsonSchema?>();

        foreach (var step in scenario.Steps)
            Visit(step);

        foreach (var fault in scenario.Faults)
        {
            var known = model.Operations.ContainsKey(fault.Target)
                || model.Channels.ContainsKey(fault.Target)
                || model.Subscriptions.ContainsKey(fault.Target)
                || model.Elements.ContainsKey(fault.Target);
            if (!known)
                issues.Add(new("", "unknown-fault-target", $"Fault target {fault.Target} is not an operation, channel, subscription or element."));
            if (fault.FailFirst is < 1)
                issues.Add(new("", "invalid-fault", $"Fault on {fault.Target} must fail at least one attempt."));
        }

        foreach (var assertion in scenario.Assertions)
        {
            if (assertion is OutcomeAssertion outcome && !seen.Contains(outcome.Step))
                issues.Add(new(outcome.Step, "unknown-step", $"Assertion {assertion.Id} names step '{outcome.Step}', which is not in the scenario."));
            if (assertion is EventCountAssertion { Expected: < 0 })
                issues.Add(new("", "invalid-assertion", $"Assertion {assertion.Id} expects a negative count."));
        }

        return issues;

        void Visit(Step step)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
                issues.Add(new(step.Id, "missing-step-id", "Every step needs an ID."));
            else if (!seen.Add(step.Id))
                issues.Add(new(step.Id, "duplicate-step-id", $"Step ID '{step.Id}' is used more than once."));

            switch (step)
            {
                case CallStep call:
                    RequireElement(step, call.From, "caller");
                    if (model.Operations.TryGetValue(call.Operation, out var operation))
                    {
                        if (operation.Retry.MaxAttempts < 1)
                            issues.Add(new(step.Id, "invalid-retry", $"Operation '{operation.Name}' must allow at least one attempt."));
                        CheckPayload(step, "request", call.Request, operation.RequestSchemaId);
                        CheckPayload(step, "response", call.Response, operation.ResponseSchemaId);
                    }
                    else
                    {
                        issues.Add(new(step.Id, "unknown-operation", $"Operation {call.Operation} does not exist."));
                    }
                    VisitAll(call.Then);
                    VisitAll(call.OnError);
                    break;

                case PublishStep publish:
                    RequireElement(step, publish.From, "publisher");
                    if (!model.Channels.ContainsKey(publish.Channel))
                        issues.Add(new(step.Id, "unknown-channel", $"Channel {publish.Channel} does not exist."));
                    if (model.Messages.TryGetValue(publish.Message, out var message))
                        CheckPayload(step, "payload", publish.Payload, message.SchemaId);
                    else
                        issues.Add(new(step.Id, "unknown-message", $"Message {publish.Message} does not exist."));

                    foreach (var (subscriptionId, handler) in publish.Handlers)
                    {
                        if (!model.Subscriptions.TryGetValue(subscriptionId, out var subscription))
                            issues.Add(new(step.Id, "unknown-subscription", $"Subscription {subscriptionId} does not exist."));
                        else if (subscription.ChannelId != publish.Channel)
                            issues.Add(new(step.Id, "subscription-not-on-channel", $"Subscription {subscriptionId} does not listen to channel {publish.Channel}."));
                        VisitAll(handler);
                    }

                    foreach (var subscription in model.Subscriptions.Values.Where(s => s.ChannelId == publish.Channel))
                    {
                        if (subscription.Retry.MaxAttempts < 1)
                            issues.Add(new(step.Id, "invalid-retry", $"Subscription {subscription.Id} must allow at least one attempt."));
                        if (subscription.DeadLetterChannelId is { } dlq && !model.Channels.ContainsKey(dlq))
                            issues.Add(new(step.Id, "unknown-channel", $"Dead-letter channel {dlq} does not exist."));
                    }
                    break;

                case WriteStep write:
                    RequireElement(step, write.By, "writer");
                    RequireElement(step, write.Store, "data store");
                    if (string.IsNullOrWhiteSpace(write.Dataset))
                        issues.Add(new(step.Id, "missing-dataset", "A write names the dataset it changes."));
                    break;

                case ParallelStep parallel:
                    foreach (var branch in parallel.Branches)
                        VisitAll(branch);
                    break;

                case BranchStep branch:
                    // Only an earlier step has an outcome when the branch is reached.
                    if (!seen.Contains(branch.WhenStep) || branch.WhenStep == branch.Id)
                        issues.Add(new(step.Id, "unknown-step", $"Branch condition names step '{branch.WhenStep}', which does not run before it."));
                    VisitAll(branch.Then);
                    VisitAll(branch.Else);
                    break;
            }
        }

        void VisitAll(IReadOnlyList<Step> steps)
        {
            foreach (var child in steps)
                Visit(child);
        }

        void RequireElement(Step step, ElementId id, string role)
        {
            if (!model.Elements.ContainsKey(id))
                issues.Add(new(step.Id, "unknown-element", $"The {role} {id} does not exist."));
        }

        void CheckPayload(Step step, string what, JsonElement? payload, SchemaId? schemaId)
        {
            if (schemaId is not { } id)
                return;

            if (!schemas.TryGetValue(id, out var schema))
            {
                if (!model.Schemas.TryGetValue(id, out var text))
                {
                    issues.Add(new(step.Id, "unknown-schema", $"Schema {id} does not exist."));
                }
                else
                {
                    try
                    {
                        schema = JsonSchema.FromText(text);
                    }
                    catch (Exception ex) when (ex is JsonException or JsonSchemaException)
                    {
                        issues.Add(new(step.Id, "invalid-schema", $"Schema {id} is not a valid JSON Schema."));
                    }
                }
                schemas[id] = schema;
            }

            if (schema is null)
                return;

            if (payload is not { } value)
            {
                issues.Add(new(step.Id, "missing-payload", $"The {what} has a schema but the step gives no value."));
                return;
            }

            var result = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid)
                return;

            var reasons = result.Details
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .Order(StringComparer.Ordinal)
                .Take(3);
            issues.Add(new(step.Id, "schema-mismatch", $"The {what} does not match its schema. {string.Join("; ", reasons)}"));
        }
    }
}
