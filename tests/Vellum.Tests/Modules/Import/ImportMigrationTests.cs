using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Import.Scans;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The import tables of a database that already holds scans and accepted facts are upgraded
/// in place: the rows written by the first import schema keep their meaning under the new keys.
/// </summary>
[Collection("Integration")]
public class ImportMigrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Rows_of_the_first_import_schema_are_upgraded_in_place()
    {
        var options = new DbContextOptionsBuilder<ImportDbContext>()
            .UseNpgsql(await TestHost.CreateDatabaseAsync(fixture)).UseSnakeCaseNamingConvention().Options;
        await using var db = new ImportDbContext(options);
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("InitialImport");

        var (project, workspace, scan, defaultScan, entity, service) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var sha = new string('a', 40);
        const string none = "{}";
        const string attributes = """{"fields":"a:b","vellum.consumers":"service:billing,service:inventory"}""";
        var accepted = "{\"name\":\"OrderCreated\",\"consumerIds\":\"" + service + "," + entity + "\"}";
        var system = Guid.NewGuid();
        foreach (var (id, requested) in new[] { (scan, "release/1"), (defaultScan, (string?)null) })
        {
            var name = requested is null ? "acme/default" : "acme/orders";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO import.scans (id, project_id, workspace_id, host, repository, requested_ref, commit_sha, include_paths, exclude_paths,
                    limits, status, tree_listed, files_listed, files_read, files_skipped, bytes_read, partial, is_complete, notes,
                    requested_by, created_at, updated_at, attempts, cancel_requested)
                VALUES ({id}, {project}, {workspace}, 'github.com', {name}, {requested}, {sha}, ARRAY[]::text[], ARRAY[]::text[],
                    {none}::jsonb, 'completed', true, 1, 1, 0, 10, false, true, '[]'::jsonb, 'user', now(), now(), 1, false)
                """);
        }
        await db.Database.ExecuteSqlAsync($"INSERT INTO import.scan_files (scan_id, path, blob_sha, size, state) VALUES ({scan}, 'src/Program.cs', {sha}, 10, 'read')");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.fact_links (project_id, host, repository, fact_key, entity_kind, entity_id, scan_id, commit_sha, attributes, accepted, evidence, accepted_by, accepted_at)
            VALUES
              ({project}, 'github.com', 'acme/orders', 'repository:github.com/acme/orders', 'element', {system}, {scan}, {sha}, {none}::jsonb, {none}::jsonb, '[]', 'user', now()),
              ({project}, 'github.com', 'acme/orders', 'message:OrderCreated', 'message', {entity}, {scan}, {sha}, {attributes}::jsonb, {accepted}::jsonb, '[]', 'user', now()),
              ({project}, 'github.com', 'acme/default', 'service:api', 'element', {service}, {defaultScan}, {sha}, {none}::jsonb, {none}::jsonb, '[]', 'user', now())
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.rejections (project_id, host, repository, fact_key, fingerprint, scan_id, rejected_by, rejected_at)
            VALUES ({project}, 'github.com', 'acme/orders', 'service:billing', 'f1', {scan}, 'user', now())
            """);

        await db.Database.MigrateAsync();

        var links = await db.FactLinks.AsNoTracking().Where(l => l.ProjectId == project).ToListAsync();
        Assert.Equal(3, links.Select(l => l.Id).Where(id => id != Guid.Empty).Distinct().Count());
        // No link has a known rule fingerprint: the next tracking scan relinks each of them.
        Assert.All(links, l => Assert.Null(l.RuleFingerprint));
        Assert.All(links, l => Assert.Equal(KeyHash.Of(l.FactKey), l.FactKeyHash));

        var repository = links.Single(l => l.Subject == Subject.Repository);
        Assert.Equal("", repository.FactKey);
        var message = links.Single(l => l.EntityId == entity);
        Assert.Equal((Subject.Fact, "message:OrderCreated"), (message.Subject, message.FactKey));
        Assert.Equal(["service:billing", "service:inventory"], ProposalJson.ReadKeys(message.ConsumerKeys));
        Assert.Equal(["fields"], ProposalJson.ReadAttributes(message.Attributes).Keys);
        Assert.Equal(ModelIndex.JoinIds([service, entity]), ProposalJson.ReadValues(message.Accepted)["consumerIds"]);

        // The accepted facts follow the ref of the scan they were accepted from. A scan
        // that named no ref followed the default ref, whose name the next scan supplies.
        var tracked = await db.TrackedRefs.AsNoTracking().Where(t => t.ProjectId == project).ToDictionaryAsync(t => t.Repository, t => t.RefName);
        Assert.Equal(("release/1", ""), (tracked["acme/orders"], tracked["acme/default"]));
        Assert.Equal("release/1", (await db.Scans.AsNoTracking().SingleAsync(s => s.Id == scan)).ResolvedRef);

        var file = await db.ScanFiles.AsNoTracking().SingleAsync(f => f.ScanId == scan);
        Assert.Equal(KeyHash.Of("src/Program.cs"), file.PathHash);
        var rejection = await db.Rejections.AsNoTracking().SingleAsync(r => r.ProjectId == project);
        Assert.Equal((KeyHash.Of("service:billing"), Subject.Fact), (rejection.FactKeyHash, rejection.Subject));
        // Each scan that was started is one unit of the day it was started on.
        Assert.Equal(2, await db.ScanCharges.CountAsync(c => c.WorkspaceId == workspace));
    }
}
