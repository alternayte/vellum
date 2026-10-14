using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;

namespace Vellum.Tests.Modules.Trace;

/// <summary>
/// <c>events.lock</c> records a step and an assertion as <c>{ id }</c>: it cannot express the
/// derived types of a polymorphic member. So a change to a step, fault or assertion type would
/// pass the contract test and break stored scenarios. This test stores one scenario that holds
/// every such type and compares the stored JSON with a committed file.
/// A difference means stored scenarios read differently. Add members as nullable, or migrate
/// the stored events; then delete the file, run the test once to write it again, and commit it.
/// </summary>
public class StoredScenarioShapeTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Every step type, every fault kind and every assertion type, with every member set.</summary>
    private static Scenario EveryType() => new(
        G(100),
        "Every type",
        Steps:
        [
            new CallStep("call", G(1), G(10), Value("""{"qty":2,"sku":"apple"}"""), Value("""{"orderId":"o-1"}"""),
                Then:
                [
                    new WriteStep("write", G(2), G(3), "orders", Value("""{"status":"accepted"}""")),
                    new PublishStep("publish", G(2), G(20), G(40), Value("""{"orderId":"o-1"}"""),
                        new Dictionary<string, string> { ["x-correlation-id"] = "c-1" },
                        new Dictionary<SubscriptionId, IReadOnlyList<Step>>
                        {
                            [G(30)] = [new BranchStep("branch", "write", StepOutcome.Succeeded,
                                Then: [new WriteStep("reserve", G(4), G(5), "stock", null)],
                                Else: [])],
                        }),
                ],
                OnError: [new WriteStep("compensate", G(2), G(3), "orders", Value("null"))]),
            new ParallelStep("parallel", [[new WriteStep("left", G(2), G(3), "a", Value("[1,2.5,true]"))], []]),
        ],
        Faults:
        [
            new Fault(G(10), FaultKind.Error, 2),
            new Fault(G(20), FaultKind.Timeout, null),
            new Fault(G(4), FaultKind.Outage, null),
        ],
        Assertions:
        [
            new OutcomeAssertion("outcome", "call", StepOutcome.Failed),
            new EventCountAssertion("count", TraceEventKind.DeadLettered, G(20), 1),
            new EventCountAssertion("count-all", TraceEventKind.Published, null, 0),
        ]);

    /// <summary>The committed file, next to this source file.</summary>
    private static string GoldenFile([CallerFilePath] string testFile = "") =>
        Path.Combine(Path.GetDirectoryName(testFile)!, "stored-scenario.golden.txt");

    [Fact]
    public async Task The_stored_scenario_event_keeps_its_shape()
    {
        var factory = fixture.Factory;
        var streamId = Guid.NewGuid();
        var scenario = EveryType();

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventStore>().Append(
                StreamId.From(streamId), ExpectedVersion.NoStream,
                [new ScenarioEvent.ScenarioSaved(G(200), scenario.Name, scenario)]);
        }

        string stored;
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT event_type, event_version, payload::text FROM deedbox.events WHERE stream_id = @id", connection);
            command.Parameters.AddWithValue("id", streamId.ToString());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "The event is stored under the stream ID");

            // Keys stay in the order jsonb keeps them; only the layout is added here.
            var payload = JsonNode.Parse(reader.GetString(2))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            stored = $"{reader.GetString(0)} v{reader.GetInt32(1)}\n{payload.ReplaceLineEndings("\n")}\n";
        }

        var golden = GoldenFile();
        if (!File.Exists(golden))
        {
            await File.WriteAllTextAsync(golden, stored);
            Assert.Fail($"Wrote {golden}. Check it and commit it; the next run compares with it.");
        }
        Assert.Equal((await File.ReadAllTextAsync(golden)).ReplaceLineEndings("\n"), stored);

        // The store reads back what it wrote, with `type` wherever jsonb put it.
        using (var scope = factory.Services.CreateScope())
        {
            var loaded = (await scope.ServiceProvider.GetRequiredService<IEventStore>()
                .Load<ScenarioState>(StreamId.From(streamId))).State.Document!;
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Assert.Equal(
                JsonNode.Parse(JsonSerializer.Serialize(scenario, options))!.ToJsonString(),
                JsonNode.Parse(JsonSerializer.Serialize(loaded, options))!.ToJsonString());
        }
    }
}
