using System.Text;
using System.Text.Json;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;

namespace Vellum.Tests.Modules.Trace;

/// <summary>
/// A payload is free JSON from an author, an import or a model. The engine gives every value
/// one byte form without throwing, and refuses the values that have no finite form before
/// they are hashed, stored or played.
/// </summary>
public class PayloadNumberTests
{
    private static readonly Guid Service = Guid.NewGuid(), Db = Guid.NewGuid();

    private static JsonElement Value(string json) =>
        JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 }).RootElement.Clone();

    private static string Canonical(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 512 }))
            CanonicalJson.Write(writer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static TraceModel Model() => new(
        new Dictionary<ElementId, string> { [Service] = "Service", [Db] = "Db" },
        new Dictionary<OperationId, OperationContract>(), new Dictionary<ChannelId, ChannelContract>(),
        new Dictionary<SubscriptionId, SubscriptionContract>(), new Dictionary<MessageId, MessageContract>(),
        new Dictionary<SchemaId, string>());

    private static Scenario Write(string change) =>
        new(Guid.NewGuid(), "Write", [new WriteStep("w", Service, Db, "orders", Value(change))], [], []);

    public static TheoryData<string> Hostile => new(
        "1e999", "-1e999", "1e200000", "[1E+400]", """{"n":1e999}""", """{"a":{"b":[-1e9999]}}""",
        new string('9', 400), "0." + new string('1', 500), "1e-999", "-0", "-0.0", "1e-30",
        "\"\\ud800\"", """{"\ud800":1}""", "\"a\\u0000b\"", """{"a":1,"a":2}""",
        string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200)));

    [Theory, MemberData(nameof(Hostile))]
    public void The_canonical_form_exists_for_every_json_value_and_is_stable(string json)
    {
        var first = Canonical(Value(json));

        Assert.Equal(first, Canonical(Value(json)));
        // The form is JSON again.
        using var parsed = JsonDocument.Parse(first, new JsonDocumentOptions { MaxDepth = 512 });
    }

    [Fact]
    public void A_number_below_the_range_of_a_decimal_keeps_its_value()
    {
        Assert.Equal("1E-30", Canonical(Value("1e-30")));
        Assert.Equal("0", Canonical(Value("-0.0")));
        Assert.Equal("1", Canonical(Value("1.0")));
        Assert.Equal("100", Canonical(Value("1e2")));
    }

    [Theory]
    [InlineData("""{"n":1e999}""", "64-bit")]
    [InlineData("""{"n":-1e200000}""", "64-bit")]
    [InlineData("""[[1E+400]]""", "64-bit")]
    [InlineData("""{"n":0.11111111111111111111111111111111111111111}""", "40 characters")]
    [InlineData("""{"n":"\ud800"}""", "valid Unicode")]
    public void The_validator_and_the_save_rule_refuse_a_payload_with_no_finite_form(string change, string reason)
    {
        var scenario = Write(change);

        var issue = Assert.Single(ScenarioValidator.Validate(Model(), scenario));
        Assert.Equal(("w", "invalid-payload"), (issue.StepId, issue.Code));
        Assert.Contains(reason, issue.Message);
        Assert.False(TraceEngine.Run(Model(), scenario).IsValid);

        var refused = ScenarioShape.Check(scenario);
        Assert.NotNull(refused);
        Assert.Equal("steps[0].change", refused.Field);
    }

    [Theory]
    [InlineData("""{"n":1e308,"m":-1e-320,"k":123456789012345678901234567890}""")]
    [InlineData("""{"a":1,"a":2}""")]
    public void A_payload_with_finite_numbers_is_valid_and_plays(string change)
    {
        var scenario = Write(change);

        Assert.Empty(ScenarioValidator.Validate(Model(), scenario));
        Assert.Null(ScenarioShape.Check(scenario));
        Assert.True(TraceEngine.Run(Model(), scenario).IsValid);
    }
}
