using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Vellum.Shared;

/// <summary>
/// Brings every module's schema up to date before the host starts, in both profiles, so
/// <c>docker compose up</c> on an empty database gives a working app. The migrations
/// also create the Deedbox tables, which Deedbox checks when the host starts.
/// An operator who applies migrations in a separate step sets
/// <c>Database:MigrateOnStartup=false</c>.
/// </summary>
public static class DatabaseMigrations
{
    // Any constant works; every Vellum instance must use the same one.
    private const long LockKey = 0x56454C4C554D;

    /// <summary>
    /// The name under which diagnostics lists the migrations. Every module's tables are in
    /// <see cref="AppDbContext"/>, each in its module's own Postgres schema, so one history
    /// holds them all.
    /// </summary>
    public const string HistoryName = "vellum";

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
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A migration first reads the history table. On an empty database that read fails
            // and logs an error, although the migration then creates the table. So the table
            // is created first.
            await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);
            await db.Database.MigrateAsync(ct);
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
