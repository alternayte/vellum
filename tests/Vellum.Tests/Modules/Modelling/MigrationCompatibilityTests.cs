using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Vellum.Modules.Modelling;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>
/// SDD §10: migrations retain old projects. Applies the pre-facts schema, inserts legacy
/// rows without the new columns, migrates to latest, then asserts the backfill and the
/// branch-scoped row identity.
/// </summary>
public class MigrationCompatibilityTests
{
    private const string PreFactsMigration = "20260625120000_AddElementIcon";

    [Fact]
    public async Task Legacy_rows_survive_fact_and_row_identity_migrations()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<ModellingDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new ModellingDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreFactsMigration);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO modelling.elements (id, project_id, branch, kind, name, status, tags)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'system', 'Legacy Orders', 'current', ARRAY[]::text[])
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO modelling.relationships (id, project_id, branch, from_id, to_id, label)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'legacy')
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO modelling.messages (id, project_id, branch, name, producer_id, consumer_ids, tags)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'LegacyPlaced', gen_random_uuid(), ARRAY[]::uuid[], ARRAY[]::text[])
            """);

        await migrator.MigrateAsync();

        var element = await db.Elements.AsNoTracking().SingleAsync(e => e.Name == "Legacy Orders");
        Assert.Equal("declared", element.FactStatus);
        Assert.NotEqual(Guid.Empty, element.RowId);

        var relationship = await db.Relationships.AsNoTracking().SingleAsync(r => r.Label == "legacy");
        Assert.Equal("structural", relationship.Kind);
        Assert.Equal("declared", relationship.FactStatus);
        Assert.NotEqual(Guid.Empty, relationship.RowId);

        var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Name == "LegacyPlaced");
        Assert.Equal("declared", message.FactStatus);
        Assert.NotEqual(Guid.Empty, message.RowId);
    }
}
