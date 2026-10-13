using System.Net;
using System.Net.Http.Json;
using Deedbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// The read models are projections of the event streams. A rebuild resets them and replays
/// every stored event, so it must reproduce the same rows: main branch, forked branch and
/// schemas. This pins the reset, the fork event and the project header on stored events.
/// </summary>
[Collection("Integration")]
public class ProjectionRebuildTests
{
    private readonly IntegrationFixture _fixture;

    public ProjectionRebuildTests(IntegrationFixture fixture) => _fixture = fixture;

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.ConfigureServices(services =>
                {
                    var cs = _fixture.ConnectionString;
                    Replace<AppIdentityDbContext>(services, cs);
                    Replace<WorkspacesDbContext>(services, cs);
                    Replace<ModellingDbContext>(services, cs);
                    Replace<DraftsDbContext>(services, cs);
                    Replace<SchemasDbContext>(services, cs);
                });
            });
    }

    private static void Replace<TContext>(IServiceCollection services, string connectionString) where TContext : DbContext
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)).ToList())
            services.Remove(descriptor);
        services.AddDbContext<TContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
    }

    [Fact]
    public async Task Rebuild_reproduces_main_branch_and_schema_rows()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "Test User" });

        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Test Project" });

        var schemaId = Guid.NewGuid();
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/schemas",
            new { id = schemaId, name = "OrderPlaced", content = """{"type":"object"}""" }));

        var ordersId = Guid.NewGuid();
        var billingId = Guid.NewGuid();
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = ordersId, kind = "system", name = "Orders", tags = new[] { "core" } }));
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/elements",
            new { id = billingId, kind = "system", name = "Billing" }));
        var patched = await client.PatchAsJsonAsync($"/api/projects/{projectId}/elements/{billingId}",
            new { name = "Billing v2", factStatus = "inferred", setProvenance = true, provenance = new { source = "repo@abc:src/Billing.cs:1-9", revision = "abc", confidence = 0.5 } });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/relationships",
            new { id = Guid.NewGuid(), fromId = ordersId, toId = billingId, label = "charges", kind = "behavioural" }));
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/messages",
            new { id = Guid.NewGuid(), name = "OrderPlaced", producerId = ordersId, consumerIds = new[] { billingId }, schemaId }));

        var draftResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/drafts",
            new { id = Guid.NewGuid(), name = "Feature branch" });
        var draft = await draftResponse.Content.ReadFromJsonAsync<DraftDto>();
        await Created(client.PostAsJsonAsync($"/api/projects/{projectId}/elements?branchId={draft!.StreamId}",
            new { id = Guid.NewGuid(), kind = "system", name = "Only on the draft" }));

        string[] reads =
        [
            $"/api/projects/{projectId}/elements",
            $"/api/projects/{projectId}/relationships",
            $"/api/projects/{projectId}/messages",
            $"/api/projects/{projectId}/elements?branchId={draft.StreamId}",
            $"/api/projects/{projectId}/relationships?branchId={draft.StreamId}",
            $"/api/projects/{projectId}/messages?branchId={draft.StreamId}",
            $"/api/projects/{projectId}/schemas",
        ];

        var before = await ReadAll(client, reads);
        Assert.Contains("Billing v2", before[0]);
        Assert.Contains("Only on the draft", before[3]);
        Assert.Contains("OrderPlaced", before[6]);

        var admin = factory.Services.GetRequiredService<IEventStoreAdmin>();
        await RebuildAsync(admin, "model_read_model");
        await RebuildAsync(admin, "schema_read_model");

        Assert.Equal(before, await ReadAll(client, reads));
    }

    /// <summary>
    /// A model stream also holds contract events, which have no read-model rows, and
    /// <c>BranchClosed</c>, on which the read model drops a branch. A rebuild replays them all:
    /// it must not stop on them, and the rows of a merged or abandoned draft must stay gone.
    /// </summary>
    [Fact]
    public async Task Rebuild_replays_contract_events_and_closed_branches()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/api/auth/register",
            new { email = $"test-{Guid.NewGuid():N}@vellum.local", password = "Test12345!", displayName = "Test User" });

        var workspaceId = Guid.NewGuid();
        await client.PostAsJsonAsync("/api/workspaces", new { id = workspaceId, name = "Test WS" });
        var projectId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { id = projectId, name = "Test Project" });
        string Url(string path, Guid? branch = null) => $"/api/projects/{projectId}/{path}" + (branch is null ? "" : $"?branchId={branch}");

        // Main: every kind of contract event (added, changed, provenance, removed).
        Guid orders = Guid.NewGuid(), operation = Guid.NewGuid(), events = Guid.NewGuid(), deadLetters = Guid.NewGuid(), subscription = Guid.NewGuid();
        await Created(client.PostAsJsonAsync(Url("elements"), new { id = orders, kind = "system", name = "Orders" }));
        await Created(client.PostAsJsonAsync(Url("operations"), new { id = operation, ownerId = orders, name = "POST /orders", transport = "http" }));
        await Created(client.PostAsJsonAsync(Url("channels"), new { id = events, name = "order-events", kind = "topic" }));
        await Created(client.PostAsJsonAsync(Url("channels"), new { id = deadLetters, name = "dlq", kind = "queue" }));
        await Created(client.PostAsJsonAsync(Url("subscriptions"), new { id = subscription, channelId = events, consumerId = orders, deadLetterChannelId = deadLetters }));
        foreach (var (path, change) in new (string, object)[]
        {
            ($"operations/{operation}", new { timeoutMs = 500, factStatus = "observed-in-source", setProvenance = true, provenance = new { source = "repo@abc:Orders.cs:1" } }),
            ($"channels/{events}", new { name = "orders.events", factStatus = "unknown" }),
            ($"subscriptions/{subscription}", new { setDeadLetterChannelId = true, factStatus = "unknown" }),
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(Url(path), change)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url($"channels/{deadLetters}"))).StatusCode);

        async Task<DraftDto> DraftAsync(string name) =>
            (await (await client.PostAsJsonAsync(Url("drafts"), new { id = Guid.NewGuid(), name })).Content.ReadFromJsonAsync<DraftDto>())!;

        // A merged draft: BranchClosed goes to main with the merged contract and element.
        var merged = await DraftAsync("Merged");
        await Created(client.PostAsJsonAsync(Url("elements", merged.StreamId), new { id = Guid.NewGuid(), kind = "system", name = "From the merged draft" }));
        await Created(client.PostAsJsonAsync(Url("channels", merged.StreamId), new { id = Guid.NewGuid(), name = "merged-channel", kind = "topic" }));
        var preview = await (await client.PostAsync(Url($"drafts/{merged.Id}/merge/preview"), null)).Content.ReadFromJsonAsync<MergePreviewResponse>();
        var execute = await client.PostAsJsonAsync(Url($"drafts/{merged.Id}/merge/execute"),
            new { resolutions = Array.Empty<object>(), expectedMainVersion = preview!.MainVersion });
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);

        // An abandoned draft: BranchClosed goes to the draft's own stream.
        var abandoned = await DraftAsync("Abandoned");
        await Created(client.PostAsJsonAsync(Url("elements", abandoned.StreamId), new { id = Guid.NewGuid(), kind = "system", name = "Only on the abandoned draft" }));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Url($"drafts/{abandoned.Id}/abandon"), null)).StatusCode);

        // An open draft with contracts of its own.
        var open = await DraftAsync("Open");
        await Created(client.PostAsJsonAsync(Url("elements", open.StreamId), new { id = Guid.NewGuid(), kind = "system", name = "Only on the open draft" }));
        await Created(client.PostAsJsonAsync(Url("operations", open.StreamId), new { id = Guid.NewGuid(), ownerId = orders, name = "Draft operation", transport = "grpc" }));

        string[] reads =
        [
            Url("elements"), Url("operations"), Url("channels"), Url("subscriptions"),
            Url("elements", open.StreamId), Url("operations", open.StreamId), Url("channels", open.StreamId),
        ];
        var before = await ReadAll(client, reads);
        Assert.Contains("From the merged draft", before[0]);
        Assert.Contains("merged-channel", before[2]);
        Assert.DoesNotContain("abandoned", before[0]);
        Assert.Contains("Only on the open draft", before[4]);

        async Task<int> ClosedBranchRowsAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModellingDbContext>();
            return await db.Elements.CountAsync(e => e.Branch == merged.StreamId || e.Branch == abandoned.StreamId);
        }
        Assert.Equal(0, await ClosedBranchRowsAsync());

        await RebuildAsync(factory.Services.GetRequiredService<IEventStoreAdmin>(), "model_read_model");

        Assert.Equal(before, await ReadAll(client, reads));
        Assert.Equal(0, await ClosedBranchRowsAsync());
    }

    private static async Task Created(Task<HttpResponseMessage> request) =>
        Assert.Equal(HttpStatusCode.Created, (await request).StatusCode);

    private static async Task<string[]> ReadAll(HttpClient client, string[] urls)
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
