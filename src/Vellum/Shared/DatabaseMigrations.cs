using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Ai.Design;
using Vellum.Modules.Ai.Usage;
using Vellum.Modules.Docs;
using Vellum.Modules.Drafts;
using Vellum.Modules.Identity;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Scoring;
using Vellum.Modules.Settings;
using Vellum.Modules.Trace.Scenarios;
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

    /// <summary>Every module that owns a schema, in the order the migrations run.</summary>
    public static readonly IReadOnlyList<(string Module, Type Context)> Modules =
    [
        ("identity", typeof(AppIdentityDbContext)),
        ("workspaces", typeof(WorkspacesDbContext)),
        ("modelling", typeof(ModellingDbContext)),
        ("views", typeof(ViewsDbContext)),
        ("docs", typeof(DocsDbContext)),
        ("drafts", typeof(DraftsDbContext)),
        ("schemas", typeof(SchemasDbContext)),
        ("scoring", typeof(ScoringDbContext)),
        ("scenarios", typeof(ScenariosDbContext)),
        ("settings", typeof(SettingsDbContext)),
        ("ai", typeof(AiUsageDbContext)),
        ("ai-design", typeof(AiDesignDbContext)),
    ];

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
            foreach (var (_, context) in Modules)
                await ((DbContext)provider.GetRequiredService(context)).Database.MigrateAsync(ct);
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
