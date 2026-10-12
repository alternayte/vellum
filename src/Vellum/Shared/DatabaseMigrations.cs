using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Scoring;
using Vellum.Modules.Views;
using Vellum.Modules.Workspaces;

namespace Vellum.Shared;

/// <summary>
/// Brings every module's schema up to date before the host starts, in both profiles, so
/// <c>docker compose up</c> on an empty database gives a working app. The modelling
/// migrations also create the Deedbox tables, which Deedbox checks when the host starts.
/// An operator who applies migrations in a separate step sets
/// <c>Database:MigrateOnStartup=false</c>.
/// </summary>
public static class DatabaseMigrations
{
    // Any constant works; every Vellum instance must use the same one.
    private const long LockKey = 0x56454C4C554D;

    public static async Task ApplyAsync(IServiceProvider services, string connectionString, CancellationToken ct = default)
    {
        // Several instances can start at once. A session-level advisory lock makes them
        // migrate one after the other; the later ones find nothing to do.
        await using var gate = new NpgsqlConnection(connectionString);
        await gate.OpenAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", gate))
        {
            take.Parameters.AddWithValue("key", LockKey);
            await take.ExecuteNonQueryAsync(ct);
        }

        try
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider;
            await provider.GetRequiredService<AppIdentityDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<WorkspacesDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<ModellingDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<ViewsDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<DocsDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<DraftsDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<SchemasDbContext>().Database.MigrateAsync(ct);
            await provider.GetRequiredService<ScoringDbContext>().Database.MigrateAsync(ct);
        }
        finally
        {
            // Explicit: a pooled connection keeps its session, and with it the lock, after
            // it is returned to the pool.
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", gate);
            release.Parameters.AddWithValue("key", LockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
