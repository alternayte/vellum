using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using static Vellum.Modules.Review.Rules.ResilienceRules;

namespace Vellum.Modules.Review.Rules;

/// <summary>The rules about contracts: schemas, payloads, producers and shared data.</summary>
internal static class ContractRules
{
    /// <summary>The validator codes that say a payload or its schema is wrong.</summary>
    private static readonly string[] PayloadCodes = ["schema-mismatch", "invalid-payload", "invalid-schema", "unknown-schema", "missing-payload", "payload-too-large"];

    public static readonly Rule[] All =
    [
        new("CON-001", 2, RuleCategory.Contracts, "An operation and a message have a schema",
            "Each operation: its owner, its request and response schema. Each message: its producer and its schema.",
            "Give the operation or the message a schema. On the path of an approved promise the concern blocks Build Ready until then, or until an accepted decision settles it.",
            MissingSchema),
        new("CON-002", 1, RuleCategory.Contracts, "A referenced schema exists",
            "Each operation and message: the schema IDs it names, against the schemas of the project.",
            "Create the schema again, or point the operation or the message at one that exists.",
            DanglingSchema),
        new("CON-003", 2, RuleCategory.Contracts, "A scenario payload is valid against its schema",
            "Each scenario, through the trace validator: the operation of every call step and the message of every publish step, and every request, response and message payload against the schema of that contract.",
            "Correct the payload or the schema.",
            InvalidPayload),
        new("CON-004", 1, RuleCategory.Contracts, "A scenario shows the producer publishing its message",
            "Each message: its producer, and the publish steps of every scenario.",
            "Add a scenario in which the producer publishes the message. Until then the model does not show that the message is ever produced.",
            UnpublishedMessage),
        new("CON-005", 2, RuleCategory.Contracts, "One element writes a dataset",
            "Each store: the write steps of every scenario, by dataset and writer.",
            "Give the dataset one writer, or record in a decision why two elements share it.",
            SharedDataset),
    ];

    private static IEnumerable<RuleOutcome> MissingSchema(ReviewFacts facts)
    {
        foreach (var operation in facts.View.Operations)
        {
            Guid id = operation.Id;
            var severity = facts.SeverityOn(id);
            if (facts.Doubt(id, operation.OwnerId) is { } doubt)
                yield return new(RuleResult.Unknown, id, [operation.OwnerId], $"The rule does not read the operation '{operation.Name}': {doubt}.", "Confirm the fact, or correct it.", severity);
            else if (operation.RequestSchemaId is null && operation.ResponseSchemaId is null)
            {
                yield return new(RuleResult.Concern, id, [operation.OwnerId],
                    $"The operation '{operation.Name}' of '{facts.Name(operation.OwnerId)}' has no request schema and no response schema, so its contract is a name only.{facts.PathNote(id)}",
                    "The result is verified when the operation names a request schema or a response schema.", severity);
            }
            else
            {
                yield return new(RuleResult.Verified, id, [operation.OwnerId, .. Schemas(operation)],
                    $"The declared model satisfies the rule: the operation '{operation.Name}' names a schema.",
                    "The result is a concern when the operation names no schema at all.", severity);
            }
        }

        foreach (var message in facts.View.Messages)
        {
            Guid id = message.Id;
            var severity = facts.SeverityOn(id);
            if (facts.Doubt(id, message.ProducerId) is { } doubt)
                yield return new(RuleResult.Unknown, id, [message.ProducerId], $"The rule does not read the message '{message.Name}': {doubt}.", "Confirm the fact, or correct it.", severity);
            else if (message.SchemaId is null)
            {
                yield return new(RuleResult.Concern, id, [message.ProducerId],
                    $"The message '{message.Name}' has no schema, so a consumer has no contract for its payload.{facts.PathNote(id)}",
                    "The result is verified when the message names a schema.", severity);
            }
            else
            {
                yield return new(RuleResult.Verified, id, [message.ProducerId, message.SchemaId.Value],
                    $"The declared model satisfies the rule: the message '{message.Name}' names a schema.",
                    "The result is a concern when the message names no schema.", severity);
            }
        }
    }

    private static IEnumerable<RuleOutcome> DanglingSchema(ReviewFacts facts)
    {
        var subjects = facts.View.Operations.Select(o => (Id: o.Id.Value, Kind: "operation", o.Name, Named: Schemas(o).ToList()))
            .Concat(facts.View.Messages.Select(m => (Id: m.Id.Value, Kind: "message", m.Name, Named: m.SchemaId is { } schema ? [schema.Value] : new List<Guid>())));
        foreach (var (id, kind, name, named) in subjects)
        {
            var severity = facts.SeverityOn(id);
            var missing = named.Where(schema => !facts.View.SchemasById.ContainsKey(schema)).ToList();
            if (named.Count == 0)
            {
                yield return new(RuleResult.NotApplicable, id, [], $"The {kind} '{name}' names no schema.", "The rule applies when the operation or the message names a schema.", severity);
            }
            else if (facts.Doubt(id) is { } doubt)
                yield return new(RuleResult.Unknown, id, named, $"The rule does not read the {kind} '{name}': {doubt}.", "Confirm the fact, or correct it.", severity);
            else if (missing.Count > 0)
            {
                yield return new(RuleResult.Concern, id, named,
                    $"The {kind} '{name}' names {Count(missing.Count, "schema")} that the project does not hold ({string.Join(", ", missing)}), so its payload has no contract.{facts.PathNote(id)}",
                    "The result is verified when every schema the contract names exists.", severity);
            }
            else
            {
                yield return new(RuleResult.Verified, id, named,
                    $"The declared model satisfies the rule: every schema that the {kind} '{name}' names exists.",
                    "The result is a concern when a named schema does not exist.", severity);
            }
        }
    }

    private static IEnumerable<RuleOutcome> InvalidPayload(ReviewFacts facts)
    {
        foreach (var scenario in facts.View.Scenarios)
        {
            Guid id = scenario.Id;
            var severity = facts.SeverityOn(id);
            var issues = ScenarioValidator.Validate(facts.Trace, scenario);
            var payload = issues.Where(i => PayloadCodes.Contains(i.Code)).ToList();
            var checkedAgainst = Validated(facts, scenario).Distinct().Order().ToList();
            // The contract of a step says which schema its payload has, so the rule reads the
            // contract of every call and publish step. A step that names nothing in the model
            // is an issue of the validator.
            var contracts = facts.Facts(facts.View.Steps[id].Select(s => s.Step).SelectMany(step => step switch
            {
                CallStep call => [call.Operation.Value],
                PublishStep publish => [publish.Message.Value],
                _ => Array.Empty<Guid>(),
            })).Distinct().Order().ToList();
            Guid[] cites = [.. checkedAgainst, .. contracts];
            if (facts.DoubtAmong(contracts) is { } doubt)
            {
                yield return new(RuleResult.Unknown, id, cites,
                    $"The rule does not read the payloads of the scenario '{scenario.Name}': {doubt}.",
                    "Confirm the fact, or correct it.", severity);
            }
            else if (payload.Count > 0)
            {
                // The sentence names the first refused payload; the fingerprint holds each of them.
                yield return new(RuleResult.Concern, id, cites,
                    $"The trace validator refuses {Count(payload.Count, "payload")} of the scenario '{scenario.Name}' (first: step '{payload[0].StepId}', {payload[0].Code}).{facts.PathNote(id)}",
                    "The result is verified when every payload of the scenario is valid against the schema of its contract.", severity, Basis: Lines(payload));
            }
            else if (issues.Count > 0)
            {
                yield return new(RuleResult.Unknown, id, cites,
                    $"The scenario '{scenario.Name}' cannot be validated on the pinned model ({issues[0].Code}), so its payloads were not all checked.",
                    "Correct the scenario so that the validator can read every step.", severity, Basis: Lines(issues));
            }
            else if (checkedAgainst.Count == 0)
            {
                yield return new(RuleResult.Unknown, id, cites,
                    $"No step of the scenario '{scenario.Name}' has a payload with a schema to validate against, so the rule checked nothing.",
                    "Give the contracts of the scenario a schema and its steps a payload.", severity);
            }
            else
            {
                yield return new(RuleResult.Verified, id, cites,
                    $"The declared model satisfies the rule: every payload of the scenario '{scenario.Name}' is valid against its schema ({Count(checkedAgainst.Count, "schema")} read).",
                    "The result is a concern when a payload is not valid against its schema.", severity);
            }
        }

        static string[] Lines(IEnumerable<ScenarioIssue> issues) =>
            [.. issues.Select(i => $"{i.StepId} {i.Code}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The schemas that the payloads of a scenario are validated against: one per payload that is present and whose contract names a schema that exists.</summary>
    private static IEnumerable<Guid> Validated(ReviewFacts facts, Scenario scenario)
    {
        foreach (var step in facts.View.Steps[scenario.Id.Value].Select(s => s.Step))
        {
            if (step is CallStep call && facts.Model.Operations.TryGetValue(call.Operation, out var operation))
            {
                if (call.Request is not null && operation.RequestSchemaId is { } request && facts.View.SchemasById.ContainsKey(request)) yield return request;
                if (call.Response is not null && operation.ResponseSchemaId is { } response && facts.View.SchemasById.ContainsKey(response)) yield return response;
            }
            else if (step is PublishStep { Payload: not null } publish && facts.Model.Messages.TryGetValue(publish.Message, out var message)
                     && message.SchemaId is { } schema && facts.View.SchemasById.ContainsKey(schema))
                yield return schema;
        }
    }

    private static IEnumerable<RuleOutcome> UnpublishedMessage(ReviewFacts facts)
    {
        var published = facts.Publishes.ToLookup(p => p.Step.Message.Value);
        foreach (var message in facts.View.Messages)
        {
            Guid id = message.Id;
            var byProducer = published[id].Where(p => p.Step.From == message.ProducerId).Select(p => p.Scenario.Id.Value).Distinct().Order().ToList();
            if (facts.Doubt(id, message.ProducerId) is { } doubt)
                yield return new(RuleResult.Unknown, id, [message.ProducerId], $"The rule does not read the message '{message.Name}': {doubt}.", "Confirm the fact, or correct it.");
            else if (byProducer.Count > 0)
            {
                yield return new(RuleResult.Verified, id, [message.ProducerId, .. byProducer],
                    $"The declared model satisfies the rule: '{facts.Name(message.ProducerId)}' publishes the message '{message.Name}' in {Count(byProducer.Count, "scenario")}.",
                    "The result is unknown when no scenario shows the producer publishing the message.");
            }
            else
            {
                yield return new(RuleResult.Unknown, id, [message.ProducerId],
                    $"The model says '{facts.Name(message.ProducerId)}' produces the message '{message.Name}', and no scenario shows it publishing the message, so the model does not show when it is produced.",
                    "Add a scenario in which the producer publishes the message.");
            }
        }
    }

    private static IEnumerable<RuleOutcome> SharedDataset(ReviewFacts facts)
    {
        var writes = facts.Writes.Where(w => !string.IsNullOrEmpty(w.Step.Dataset)).ToLookup(w => w.Step.Store.Value);
        foreach (var store in facts.View.Elements.Where(e => e.Kind == ElementKind.Store))
        {
            Guid id = store.Id;
            var datasets = writes[id].GroupBy(w => w.Step.Dataset, StringComparer.Ordinal)
                .Select(g => (Dataset: g.Key, Writers: g.Select(w => w.Step.By.Value).Distinct().Order().ToList()))
                .OrderBy(d => d.Dataset, StringComparer.Ordinal).ToList();
            var shared = datasets.Where(d => d.Writers.Count > 1).ToList();
            var writers = datasets.SelectMany(d => d.Writers).Distinct().Order().ToList();
            if (datasets.Count == 0)
            {
                yield return new(RuleResult.Unknown, id, [],
                    $"No scenario writes to the store '{store.Name}', so the model does not show who owns its data.",
                    "Add the write steps of the scenarios that change the store.");
            }
            else if (facts.Doubt([id, .. writers.Select(w => (Guid?)w)]) is { } doubt)
                yield return new(RuleResult.Unknown, id, writers, $"The rule does not read the writers of the store '{store.Name}': {doubt}.", "Confirm the fact, or correct it.");
            else if (shared.Count > 0)
            {
                yield return new(RuleResult.Concern, id, [.. shared.SelectMany(d => d.Writers).Distinct().Order()],
                    $"The dataset '{shared[0].Dataset}' of the store '{store.Name}' has {Count(shared[0].Writers.Count, "writer")} in the scenarios ({string.Join(", ", shared[0].Writers.Select(w => $"'{facts.Name(w)}'"))}), so they are coupled through its data{(shared.Count > 1 ? $"; {Count(shared.Count - 1, "more dataset")} of the store are shared too" : "")}.",
                    "The result is verified when each dataset of the store has one writer in the scenarios.",
                    // The sentence names the first shared dataset; the fingerprint holds each with its writers.
                    Basis: [.. shared.Select(d => $"{d.Dataset}: {string.Join(", ", d.Writers)}")]);
            }
            else
            {
                yield return new(RuleResult.Verified, id, writers,
                    $"The declared model satisfies the rule: each of the {Count(datasets.Count, "dataset")} of the store '{store.Name}' that scenarios write has one writer.",
                    "The result is a concern when two elements write the same dataset.");
            }
        }
    }

    private static IEnumerable<Guid> Schemas(OperationState operation) =>
        new Guid?[] { operation.RequestSchemaId?.Value, operation.ResponseSchemaId?.Value }.OfType<Guid>();
}
