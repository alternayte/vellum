using System.Net;
using System.Net.Http.Json;
using Deedbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Variants;

namespace Vellum.Tests.Modules.Drafts;

/// <summary>
/// A merged or abandoned draft is history. Its branch has no read-model rows, a read of it
/// finds nothing, and a projection rebuild gives the same end state. A variant stays.
/// </summary>
[Collection("Integration")]
public class ClosedDraftTests(IntegrationFixture fixture)
{
    private static string Url(Guid projectId, string path, Guid? branchId = null) =>
        $"/api/projects/{projectId}/{path}" + (branchId is null ? "" : $"?branchId={branchId}");

    [Fact]
    public async Task A_closed_draft_has_no_rows_and_no_reads_before_and_after_a_rebuild()
    {
        using var factory = TestHost.Create(fixture);
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var orders = Guid.NewGuid();
        var billing = Guid.NewGuid();
        var relationship = Guid.NewGuid();
        var message = Guid.NewGuid();
        var scenario = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = orders, kind = "system", name = "Orders" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = billing, kind = "system", name = "Billing" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "relationships"), new { id = relationship, fromId = orders, toId = billing, label = "charges" }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "messages"), new { id = message, name = "OrderPlaced", producerId = orders, consumerIds = new[] { billing } }));
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "scenarios"),
            new { id = scenario, name = "Empty", steps = Array.Empty<object>(), faults = Array.Empty<object>(), assertions = Array.Empty<object>() }));

        var merged = await OpenDraftWithAnElementAsync(client, projectId, "Merged on the draft");
        var abandoned = await OpenDraftWithAnElementAsync(client, projectId, "Abandoned on the draft");
        var variant = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "variants"), new { id = Guid.NewGuid(), name = "What if" })))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        // An open draft reads as a full model: three elements, one relationship, one message.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url(projectId, $"elements/{orders}", merged.StreamId))).StatusCode);
        Assert.Equal(5, await RowsAsync(factory, merged.StreamId));

        var preview = (await (await client.PostAsync(Url(projectId, $"drafts/{merged.Id}/merge/preview"), null)).Content.ReadFromJsonAsync<MergePreviewResponse>())!;
        var execute = await client.PostAsJsonAsync(Url(projectId, $"drafts/{merged.Id}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview.MainVersion });
        Assert.True(execute.StatusCode == HttpStatusCode.OK, await execute.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url(projectId, $"drafts/{abandoned.Id}/abandon"), null)).StatusCode);

        string[] kept =
        [
            Url(projectId, "elements"), Url(projectId, "relationships"), Url(projectId, "messages"),
            Url(projectId, "elements", variant.StreamId), Url(projectId, "relationships", variant.StreamId), Url(projectId, "messages", variant.StreamId),
        ];
        var before = await ReadAllAsync(client, kept);
        Assert.Contains("Merged on the draft", before[0]);
        Assert.DoesNotContain("Abandoned on the draft", before[0]);

        await AssertClosedAsync(factory, client, projectId, merged.StreamId);
        await AssertClosedAsync(factory, client, projectId, abandoned.StreamId);

        await RebuildAsync(factory.Services.GetRequiredService<IEventStoreAdmin>(), "model_read_model");

        await AssertClosedAsync(factory, client, projectId, merged.StreamId);
        await AssertClosedAsync(factory, client, projectId, abandoned.StreamId);
        Assert.Equal(before, await ReadAllAsync(client, kept));

        async Task AssertClosedAsync(WebApplicationFactory<Program> app, HttpClient http, Guid project, Guid branch)
        {
            string[] reads =
            [
                "elements", $"elements/{orders}", "relationships", $"relationships/{relationship}",
                "messages", $"messages/{message}", "operations", "channels", "subscriptions",
            ];
            foreach (var read in reads)
                Assert.Equal((read, HttpStatusCode.NotFound), (read, (await http.GetAsync(Url(project, read, branch))).StatusCode));
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(Url(project, $"scenarios/{scenario}/run", branch), null)).StatusCode);
            Assert.Equal(0, await RowsAsync(app, branch));
        }
    }

    private static async Task<DraftDto> OpenDraftWithAnElementAsync(HttpClient client, Guid projectId, string elementName)
    {
        var draft = (await (await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "drafts"), new { id = Guid.NewGuid(), name = elementName })))
            .Content.ReadFromJsonAsync<DraftDto>())!;
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements", draft.StreamId), new { id = Guid.NewGuid(), kind = "system", name = elementName }));
        return draft;
    }

    /// <summary>The read-model rows of one branch.</summary>
    private static async Task<int> RowsAsync(WebApplicationFactory<Program> factory, Guid branch)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModellingDbContext>();
        return await db.Elements.CountAsync(e => e.Branch == branch)
            + await db.Relationships.CountAsync(r => r.Branch == branch)
            + await db.Messages.CountAsync(m => m.Branch == branch);
    }

    private static async Task<string[]> ReadAllAsync(HttpClient client, string[] urls)
    {
        var bodies = new string[urls.Length];
        for (var i = 0; i < urls.Length; i++)
            bodies[i] = await client.GetStringAsync(urls[i]);
        return bodies;
    }

    /// <summary>Queues the rebuild, then waits until the projection runs inline again.</summary>
    private static async Task RebuildAsync(IEventStoreAdmin admin, string projection)
    {
        var jobId = await admin.RebuildAsync(projection);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            var job = await admin.GetJobAsync(jobId, timeout.Token);
            Assert.NotEqual(JobState.Failed, job!.Status);

            if (job.Status == JobState.Done)
            {
                var status = await admin.GetStatusAsync(timeout.Token);
                var consumer = status.Consumers.Single(c => c.Name == projection);
                Assert.NotEqual(ConsumerState.Stalled, consumer.Status);
                if (consumer.Status == ConsumerState.Running)
                    return;
            }

            await Task.Delay(100, timeout.Token);
        }
    }
}
