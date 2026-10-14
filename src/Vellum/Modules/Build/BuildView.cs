using System.Text.Json;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Review;
using Vellum.Modules.Review.Decisions;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;

namespace Vellum.Modules.Build;

/// <summary>One step of a scenario in document order, with where it sits.</summary>
/// <param name="Row">1 for the first step of the scenario, counting depth first.</param>
/// <param name="ParentRow">The row of the step that holds this one, or 0 at the top.</param>
/// <param name="Role">Why the step is inside its parent: <c>callee</c>, <c>on-error</c>, <c>handler</c>, <c>branch</c>, <c>then</c> or <c>else</c>.</param>
/// <param name="Handler">The subscription whose consumer runs the step, for the role <c>handler</c>.</param>
internal sealed record FlatStep(Guid ScenarioId, int Row, int ParentRow, string Role, Step Step, Guid? Handler);

/// <summary>
/// The input in one fixed order, with everything the documents derive from it. Every list is
/// sorted by a name and then by ID, so the order of the input never shows in an export.
/// </summary>
internal sealed class BuildView
{
    private readonly Lazy<IReadOnlyDictionary<Guid, TraceResult>> _playbacks;
    private readonly Lazy<IReadOnlyList<FindingView>> _review;
    private readonly BuildView? _whole;
    private readonly string _outside;

    /// <param name="whole">
    /// For a view of part of the pinned state: the view it is part of. <see cref="Name"/> then
    /// names a thing that the part leaves out by its name there, marked with <paramref name="outside"/>.
    /// </param>
    public BuildView(BuildInput input, BuildView? whole = null, string outside = "out of scope")
    {
        Input = input;
        _whole = whole;
        _outside = outside;
        Model = input.Model;
        Elements = [.. Model.Elements.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id.Value)];
        Operations = [.. Model.Operations.Values.OrderBy(o => Name(o.OwnerId), StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id.Value)];
        Channels = [.. Model.Channels.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.Value)];
        Subscriptions = [.. Model.Subscriptions.Values.OrderBy(s => Name(s.ChannelId), StringComparer.Ordinal).ThenBy(s => Name(s.ConsumerId), StringComparer.Ordinal).ThenBy(s => s.Id.Value)];
        Bindings = [.. Model.Bindings.Values.OrderBy(b => Name(b.SourceChannelId), StringComparer.Ordinal).ThenBy(b => Name(b.TargetChannelId), StringComparer.Ordinal).ThenBy(b => b.Id.Value)];
        Messages = [.. Model.Messages.Values.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id.Value)];
        Relationships = [.. Model.Relationships.Values.OrderBy(r => r.Id.Value)];
        Schemas = [.. input.Schemas.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id)];
        SchemasById = Schemas.ToDictionary(s => s.Id);
        Scenarios = [.. input.Scenarios.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.Value)];
        Promises = [.. input.Promises.OrderBy(p => p.RequirementId.Length).ThenBy(p => p.RequirementId, StringComparer.Ordinal).ThenBy(p => p.Id)];
        Requirements = [.. Promises.Where(p => p.State == PromiseLifecycle.Approved)];

        _playbacks = new Lazy<IReadOnlyDictionary<Guid, TraceResult>>(() =>
        {
            var traceModel = TraceModelBuilder.Build(Model, Schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content));
            return Scenarios.ToDictionary(s => s.Id.Value, s => TraceEngine.Run(traceModel, s));
        });
        Steps = Scenarios.ToDictionary(s => s.Id.Value, s => (IReadOnlyList<FlatStep>)Flatten(s));
        AllSteps = [.. Scenarios.SelectMany(s => Steps[s.Id.Value])];
        Decisions = new DecisionBook(input.Decisions);
        _review = new Lazy<IReadOnlyList<FindingView>>(() => Reviewed ? ReviewEngine.Evaluate(this) : []);
    }

    public BuildInput Input { get; }
    public ModelState Model { get; }
    public IReadOnlyList<ElementState> Elements { get; }
    public IReadOnlyList<OperationState> Operations { get; }
    public IReadOnlyList<ChannelState> Channels { get; }
    public IReadOnlyList<SubscriptionState> Subscriptions { get; }
    public IReadOnlyList<BindingState> Bindings { get; }
    public IReadOnlyList<MessageState> Messages { get; }
    public IReadOnlyList<RelationshipState> Relationships { get; }
    public IReadOnlyList<BuildSchema> Schemas { get; }
    public IReadOnlyDictionary<Guid, BuildSchema> SchemasById { get; }
    public IReadOnlyList<Scenario> Scenarios { get; }
    public IReadOnlyList<PromiseDto> Promises { get; }
    /// <summary>
    /// The promises that bind: those whose revision in force is approved. <c>Current</c> of
    /// each is that revision. A promise that was never approved and a retired promise are in
    /// <see cref="Promises"/> only, and every document lists them apart, as not binding.
    /// </summary>
    public IReadOnlyList<PromiseDto> Requirements { get; }
    /// <summary>Each scenario played on the pinned model. Played once, when first read.</summary>
    public IReadOnlyDictionary<Guid, TraceResult> Playbacks => _playbacks.Value;
    public IReadOnlyDictionary<Guid, IReadOnlyList<FlatStep>> Steps { get; }
    public IReadOnlyList<FlatStep> AllSteps { get; }
    /// <summary>The decisions of the pinned state, with which of them is in force.</summary>
    public DecisionBook Decisions { get; }
    /// <summary>
    /// False for a pinned state of the format <c>vellum-bundle/2</c>, which names no decision
    /// stream: it was exported before the review existed and is generated without it.
    /// </summary>
    public bool Reviewed => Input.Pinned.Decisions is not null;
    /// <summary>
    /// The findings of the review rules on this input, with the decisions that settle them.
    /// This is the one place where the Build module asks the Review module. Computed once,
    /// when first read; empty when the state is not <see cref="Reviewed"/>.
    /// </summary>
    public IReadOnlyList<FindingView> Review => _review.Value;

    /// <summary>
    /// The name of whatever the ID names in the model or the scenarios. In a view of part of
    /// the pinned state, a thing that the part leaves out is named from the view it is part of,
    /// with the mark of the part (for example "out of scope"), and a thing that no view holds is
    /// "not in the model". In a view of the whole state, a thing that it does not hold is named
    /// by its ID, as it always was: an export keeps its bytes.
    /// </summary>
    public string Name(Guid id) =>
        NameIn(id) ?? (_whole is null ? id.ToString() : _whole.NameIn(id) is { } name ? $"{name}, {_outside}" : _whole.Outside(id));

    private string Outside(Guid id) =>
        _whole is null ? "not in the model" : _whole.NameIn(id) is { } name ? $"{name}, {_outside}" : _whole.Outside(id);

    private string? NameIn(Guid id)
    {
        if (Model.Elements.TryGetValue(id, out var element)) return element.Name;
        if (Model.Operations.TryGetValue(id, out var operation)) return operation.Name;
        if (Model.Channels.TryGetValue(id, out var channel)) return channel.Name;
        if (Model.Messages.TryGetValue(id, out var message)) return message.Name;
        if (Model.Subscriptions.TryGetValue(id, out var subscription)) return ContractDecider.SubscriptionLabel(Model, subscription);
        if (Model.Bindings.TryGetValue(id, out var binding)) return ContractDecider.BindingLabel(Model, binding);
        if (SchemasById is not null && SchemasById.TryGetValue(id, out var schema)) return schema.Name;
        return Input.Scenarios.FirstOrDefault(s => s.Id.Value == id)?.Name;
    }

    /// <summary>The messages that scenarios publish to each channel, and who publishes them: the model has no publication of its own.</summary>
    public IEnumerable<(Guid Publisher, Guid Channel, Guid Message)> Publications() =>
        AllSteps.Select(s => s.Step).OfType<PublishStep>()
            .Select(p => (p.From.Value, p.Channel.Value, p.Message.Value))
            .Distinct()
            .OrderBy(p => p.Item1).ThenBy(p => p.Item2).ThenBy(p => p.Item3);

    private static List<FlatStep> Flatten(Scenario scenario)
    {
        var rows = new List<FlatStep>();
        Add(scenario.Steps, 0, "", null);
        return rows;

        void Add(IReadOnlyList<Step>? steps, int parent, string role, Guid? handler)
        {
            foreach (var step in steps ?? [])
            {
                if (step is null)
                    continue;
                var row = rows.Count + 1;
                rows.Add(new FlatStep(scenario.Id, row, parent, role, step, handler));
                switch (step)
                {
                    case CallStep call:
                        Add(call.Then, row, "callee", null);
                        Add(call.OnError, row, "on-error", null);
                        break;
                    case PublishStep publish:
                        foreach (var (subscription, handlerSteps) in (publish.Handlers ?? new Dictionary<SubscriptionId, IReadOnlyList<Step>>()).OrderBy(h => h.Key.Value))
                            Add(handlerSteps, row, "handler", subscription.Value);
                        break;
                    case ParallelStep parallel:
                        foreach (var branch in parallel.Branches ?? [])
                            Add(branch, row, "branch", null);
                        break;
                    case BranchStep branch:
                        Add(branch.Then, row, "then", null);
                        Add(branch.Else, row, "else", null);
                        break;
                }
            }
        }
    }
}

/// <summary>The byte forms of an export: canonical JSON, YAML with <c>\n</c> line ends, and SHA-256.</summary>
internal static class BuildText
{
    public static readonly JsonSerializerOptions Json = new(PromiseJson.Options);

    private static readonly System.Text.Encodings.Web.JavaScriptEncoder Encoder =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>Sorted keys, one spelling per number, two-space indent, <c>\n</c> line ends and one at the end.</summary>
    public static byte[] CanonicalJson<T>(T value) => CanonicalJson(JsonSerializer.SerializeToElement(value, Json));

    public static byte[] CanonicalJson(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n", Encoder = Encoder }))
            Trace.Engine.CanonicalJson.Write(writer, element);
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>A JSON value as text for a Markdown code block.</summary>
    public static string Pretty(JsonElement element) => System.Text.Encoding.UTF8.GetString(CanonicalJson(element)).TrimEnd('\n');

    /// <summary>The document as YAML that reads back the same in YAML 1.1 and 1.2 readers. See <see cref="YamlWriter"/>.</summary>
    /// <exception cref="YamlNumberException">The document holds a number that YAML cannot hold with the same value.</exception>
    public static byte[] Yaml(object document) => System.Text.Encoding.UTF8.GetBytes(YamlWriter.Write(document));

    /// <summary>The text as a JSON document, or null when it is not JSON. The caller disposes the document.</summary>
    public static JsonDocument? TryParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>A JSON value as plain objects for the YAML writer, with sorted keys. A number keeps its source text.</summary>
    public static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ToMap(value.EnumerateObject()
            .GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new KeyValuePair<string, object?>(g.Key, Plain(g.Last().Value)))),
        JsonValueKind.Array => value.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => new YamlNumber(value.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static OrderedDictionary<string, object?> ToMap(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var map = new OrderedDictionary<string, object?>();
        foreach (var (key, value) in entries)
            map[key] = value;
        return map;
    }

    public static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] entries) =>
        ToMap(entries.Select(e => new KeyValuePair<string, object?>(e.Key, e.Value)));

    /// <summary>A key for a YAML map and a JSON pointer: letters, digits, <c>_</c>, <c>.</c> and <c>-</c> only.</summary>
    public static string Key(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    /// <summary>One key per item: the cleaned name, with the start of the ID added where two items share it.</summary>
    public static Dictionary<Guid, string> Keys(IEnumerable<(Guid Id, string Name)> items)
    {
        var list = items.OrderBy(i => i.Id).ToList();
        var shared = list.GroupBy(i => Key(i.Name), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return list.ToDictionary(i => i.Id, i => shared.Contains(Key(i.Name)) ? $"{Key(i.Name)}.{i.Id}" : Key(i.Name));
    }

    /// <summary>
    /// The channel and every channel a message published to it can reach through bindings.
    /// Routing keys are not read here, as in <c>ChannelRouting.Reachable</c>.
    /// </summary>
    public static IReadOnlySet<ChannelId> Reachable(ModelState model, ChannelId channel)
    {
        var targets = model.Bindings.Values.ToLookup(b => b.SourceChannelId, b => b.TargetChannelId);
        var reached = new HashSet<ChannelId> { channel };
        var open = new Queue<ChannelId>([channel]);
        while (open.TryDequeue(out var current))
        {
            foreach (var target in targets[current])
            {
                if (reached.Add(target))
                    open.Enqueue(target);
            }
        }

        return reached;
    }
}
