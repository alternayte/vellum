using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Modelling.Model;

namespace Vellum.Tests.Modules.Ai.Design;

/// <summary>
/// The schemas that go to a provider's structured-output mode stay inside what every vendor
/// documents. The limits below are the vendors' own numbers; the margins are this project's.
/// </summary>
public class DesignSchemaTests
{
    // Anthropic, "Explicit limits": "Parameters with union types: 16 — Total parameters that use anyOf or
    // type arrays (for example, "type": ["string", "null"]) across all strict schemas", and "Optional
    // parameters: 24 — Each parameter not listed in required counts toward this limit".
    // Not supported: recursive schemas, minimum/maximum/multipleOf, minLength/maxLength, array constraints
    // beyond minItems 0 or 1, additionalProperties other than false.
    // https://platform.claude.com/docs/en/build-with-claude/structured-outputs
    private const int AnthropicMaxUnionParameters = 16;
    private const int AnthropicMaxOptionalParameters = 24;

    // OpenAI, "Supported schemas": every field required; additionalProperties false; the root is an
    // object and not anyOf; "up to 5000 object properties total, with up to 10 levels of nesting";
    // "up to 1000 enum values across all enum properties"; the total length of all property names,
    // definition names, enum values and const values is at most 120,000 characters.
    // Not supported: allOf, not, dependentRequired, dependentSchemas, if, then, else.
    // https://developers.openai.com/api/docs/guides/structured-outputs
    private const int OpenAiMaxNesting = 10;
    private const int OpenAiMaxProperties = 5_000;
    private const int OpenAiMaxEnumValues = 1_000;
    private const int OpenAiMaxNameChars = 120_000;

    // Gemini: "supports a subset of the JSON Schema specification"; "Very large or deeply nested schemas
    // may be rejected." The guide gives no number. Its listed keywords include every one allowed below.
    // https://ai.google.dev/gemini-api/docs/structured-output
    private static readonly string[] AllowedKeywords = ["type", "properties", "required", "additionalProperties", "items", "enum"];
    private static readonly string[] AllowedTypes = ["object", "array", "string", "integer", "number", "boolean"];

    // This project's margins: no union and no optional property at all, and far below the size limits.
    private const int MaxNesting = OpenAiMaxNesting / 2;
    private const int MaxProperties = 100;
    private const int MaxEnumValues = 50;
    private const int MaxNameChars = 2_000;

    public static TheoryData<string> Schemas => new("flow", "architecture", "critique", "connection-test");

    private static JsonElement Schema(string name) => name switch
    {
        "flow" => DesignSchemas.Flow,
        "architecture" => DesignSchemas.Architecture,
        "critique" => Vellum.Modules.Review.Critique.CritiqueSchema.Schema,
        _ => AiClient.ConnectionTestSchema,
    };

    private sealed class Tally
    {
        public int Unions, Optional, Properties, EnumValues, NameChars, Depth;
    }

    [Theory, MemberData(nameof(Schemas))]
    public void A_schema_for_structured_output_is_inside_the_limits_of_every_vendor(string name)
    {
        var schema = Schema(name);
        var tally = new Tally();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Walk(schema, 1, tally);

        Assert.Equal(0, tally.Unions);
        Assert.Equal(0, tally.Optional);
        Assert.True(tally.Unions <= AnthropicMaxUnionParameters && tally.Optional <= AnthropicMaxOptionalParameters);
        Assert.InRange(tally.Depth, 1, MaxNesting);
        Assert.InRange(tally.Properties, 1, Math.Min(MaxProperties, OpenAiMaxProperties));
        Assert.InRange(tally.EnumValues, 0, Math.Min(MaxEnumValues, OpenAiMaxEnumValues));
        Assert.InRange(tally.NameChars, 1, Math.Min(MaxNameChars, OpenAiMaxNameChars));
    }

    private static void Walk(JsonElement node, int depth, Tally tally)
    {
        Assert.Equal(JsonValueKind.Object, node.ValueKind);
        tally.Depth = Math.Max(tally.Depth, depth);
        Assert.All(node.EnumerateObject(), keyword => Assert.Contains(keyword.Name, AllowedKeywords));

        // One type, written as a string. A type array is a union.
        var type = node.GetProperty("type");
        if (type.ValueKind != JsonValueKind.String)
        {
            tally.Unions++;
            return;
        }
        Assert.Contains(type.GetString(), AllowedTypes);

        if (node.TryGetProperty("enum", out var values))
        {
            Assert.Equal("string", type.GetString());
            Assert.All(values.EnumerateArray(), value => Assert.Equal(JsonValueKind.String, value.ValueKind));
            tally.EnumValues += values.GetArrayLength();
            tally.NameChars += values.EnumerateArray().Sum(value => value.GetString()!.Length);
        }

        switch (type.GetString())
        {
            case "object":
                Assert.False(node.GetProperty("additionalProperties").GetBoolean());
                var required = node.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToList();
                var properties = node.GetProperty("properties").EnumerateObject().ToList();
                tally.Optional += properties.Count(p => !required.Contains(p.Name));
                Assert.Equal(properties.Select(p => p.Name), required);
                tally.Properties += properties.Count;
                tally.NameChars += properties.Sum(p => p.Name.Length);
                foreach (var property in properties)
                    Walk(property.Value, depth + 1, tally);
                break;
            case "array":
                Walk(node.GetProperty("items"), depth + 1, tally);
                break;
            default:
                Assert.False(node.TryGetProperty("properties", out _));
                Assert.False(node.TryGetProperty("items", out _));
                break;
        }
    }

    [Fact]
    public void The_prompts_state_the_sentinel_values_that_the_schemas_use()
    {
        Assert.Contains("No field is ever null", DesignPrompts.ProposeFlow["system"]);
        Assert.Contains("0 fails all", DesignPrompts.ProposeFlow["system"]);
        Assert.Contains("\"expectedCount\" -1", DesignPrompts.ProposeFlow["system"]);
        Assert.Contains("No field is ever null", DesignPrompts.InferArchitecture["system"]);
        Assert.Contains("No field is ever null", Vellum.Modules.Review.Critique.ReviewPrompts.Critique["system"]);
        Assert.Equal(0, DesignSchemas.NoFailFirst);
        Assert.Equal(-1, DesignSchemas.NoExpectedCount);
    }

    [Fact]
    public void An_answer_in_the_sentinel_form_reads_as_null_and_an_old_answer_with_nulls_still_reads()
    {
        var sentinel = new FlowOutput("Flow", "",
            [new FlowStepOutput("s", "", "root", "", 1, "write", "", "", "", "", "E1", "E2", "orders", "", "", "", "", "", "")],
            [new FlowFaultOutput("E1", "error", 0), new FlowFaultOutput("E2", "error", 2)],
            [new FlowAssertionOutput("a", "outcome", "s", "succeeded", "", "", -1), new FlowAssertionOutput("b", "event-count", "", "", "DataWritten", "", 0)],
            [], [], "", []);

        var (output, issues) = FlowAssembler.Normalize(sentinel);

        Assert.Empty(issues);
        var step = Assert.Single(output.Steps);
        Assert.Equal((null, null, null, null, null), (step.ParentStepId, step.SlotKey, step.From, step.ChangeJson, step.WhenStep));
        Assert.Equal([null, 2], output.Faults.Select(f => f.FailFirst));
        Assert.Equal([null, 0], output.Assertions.Select(a => a.ExpectedCount));
        Assert.Equal([null, null], output.Assertions.Select(a => a.Target));

        // A proposal stored before the sentinels holds nulls. It normalises to the same answer.
        var nulls = sentinel with
        {
            Steps = [new FlowStepOutput("s", null, "root", null, 1, "write", null, null, null, null, "E1", "E2", "orders", null, null, null, null, null, null)],
            Faults = [new FlowFaultOutput("E1", "error", null), new FlowFaultOutput("E2", "error", 2)],
            Assertions = [new FlowAssertionOutput("a", "outcome", "s", "succeeded", null, null, null), new FlowAssertionOutput("b", "event-count", null, null, "DataWritten", null, 0)],
        };
        Assert.Equal(
            JsonSerializer.Serialize(output), JsonSerializer.Serialize(FlowAssembler.Normalize(nulls).Output));
    }
}

/// <summary>The alias table of one request: what it lists first, what it leaves out, and what it never lists.</summary>
public class ModelAliasesTests
{
    private static readonly PrivateGate Open = new(new HashSet<Guid>(), false);

    private static ElementState Element(Guid id, string name, string? description = null, FactStatus status = FactStatus.Declared) =>
        new(id, ElementKind.System, name, description, null, null, ElementStatus.Current, null, [], null, status);

    private static ModelState Model(IEnumerable<ElementState> elements) =>
        ModelState.Initial with { Elements = elements.ToImmutableDictionary(e => e.Id) };

    [Fact]
    public void A_large_model_fills_the_table_from_what_the_request_needs_and_counts_the_rest()
    {
        Guid required = Guid.NewGuid(), selected = Guid.NewGuid(), named = Guid.NewGuid();
        var filler = Enumerable.Range(0, 3_000).Select(i => Element(Guid.NewGuid(), $"Filler {i:D4}", new string('d', 300)));
        // By name these three come last, after everything that fits.
        var model = Model([.. filler,
            Element(required, "zz required"), Element(selected, "zz selected"), Element(named, "zz Ledger archive")]);

        var aliases = ModelAliases.Build(
            model, new Dictionary<SchemaId, string>(),
            AliasNeeds.For("Move the ledger to cold storage.", [required], [selected]), Open);

        Assert.NotNull(aliases.AliasOf(required));
        Assert.NotNull(aliases.AliasOf(selected));
        Assert.NotNull(aliases.AliasOf(named));
        Assert.True(aliases.Omitted.Elements > 0);
        Assert.Equal(3_003, aliases.ByAlias.Count + aliases.Omitted.Elements);
        Assert.InRange(Encoding.UTF8.GetByteCount(aliases.Table), ModelAliases.MaxTableBytes / 2, ModelAliases.MaxTableBytes + 1_024);
        Assert.Equal(aliases.Omitted.Elements, (int)JsonNode.Parse(aliases.Table)!["omitted"]!["elements"]!);

        // The same model and the same request give the same table.
        Assert.Equal(aliases.Table, ModelAliases.Build(
            model, new Dictionary<SchemaId, string>(), AliasNeeds.For("Move the ledger to cold storage.", [required], [selected]), Open).Table);
    }

    [Fact]
    public void A_private_sourced_thing_is_not_listed_unless_the_setting_allows_it_and_then_the_table_is_private()
    {
        Guid secret = Guid.NewGuid(), open = Guid.NewGuid();
        var model = Model([Element(secret, "Vault", "From a private repository", FactStatus.ObservedInSource), Element(open, "Shop")]);
        var ids = new HashSet<Guid> { secret };

        var closed = ModelAliases.Build(model, new Dictionary<SchemaId, string>(), AliasNeeds.For("Vault", [secret]), new PrivateGate(ids, false));
        Assert.Null(closed.AliasOf(secret));
        Assert.DoesNotContain("Vault", closed.Table);
        Assert.Equal((1, AiDataClass.Public), (closed.OmittedPrivateCount, closed.DataClass));
        Assert.Equal(1, (int)JsonNode.Parse(closed.Table)!["withheld"]!);

        var allowed = ModelAliases.Build(model, new Dictionary<SchemaId, string>(), AliasNeeds.For("Vault"), new PrivateGate(ids, true));
        Assert.NotNull(allowed.AliasOf(secret));
        Assert.Equal((0, AiDataClass.PrivateRepository), (allowed.OmittedPrivateCount, allowed.DataClass));
    }

    [Fact]
    public void A_proposed_name_with_a_hidden_character_or_a_look_alike_letter_is_refused()
    {
        var model = Model([Element(Guid.NewGuid(), "Orders")]);

        Assert.Null(ProposedNames.Problem(model, "element", "Orders"));
        Assert.Null(ProposedNames.Problem(model, "element", "orders"));
        Assert.Null(ProposedNames.Problem(model, "element", "Payments"));
        // U+041E CYRILLIC CAPITAL LETTER O.
        Assert.Contains("'Orders'", ProposedNames.Problem(model, "element", "Оrders"));
        // U+039F GREEK CAPITAL LETTER OMICRON.
        Assert.Contains("'Orders'", ProposedNames.Problem(model, "element", "Οrders"));
        Assert.Contains("does not show", ProposedNames.Problem(model, "element", "Pay​ments"));
        Assert.Contains("does not show", ProposedNames.Problem(model, "element", "‮Payments"));
        // e + U+0301 is é in form NFC.
        Assert.Equal("Café", ProposedNames.Normalize("Café"));
    }
}
