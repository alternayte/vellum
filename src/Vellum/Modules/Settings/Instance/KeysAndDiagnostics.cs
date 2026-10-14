using System.Security.Claims;
using System.Security.Cryptography;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Settings.Crypto;
using Vellum.Modules.Settings.Secrets;
using Vellum.Shared;
using Vellum.Shared.Telemetry;

namespace Vellum.Modules.Settings.Instance;

/// <summary>
/// <paramref name="Rewrapped"/> counts the rows that this call moved under the current master
/// key. <paramref name="Remaining"/> counts the rows that are still under another key.
/// Remove the previous master key only when it is 0; <paramref name="Message"/> says so.
/// </summary>
public sealed record RotateKeysResponse(int Rewrapped, int Remaining, string Message);

/// <summary>
/// Moves every stored key under the current master key. The operator starts the app with the
/// new key as <c>Vellum__MasterKey</c> and the old one as <c>Vellum__PreviousMasterKey</c>,
/// calls this until nothing remains, and then removes the previous key.
/// </summary>
public static class RotateKeys
{
    public const int BatchSize = 100;

    // An instance that still runs with the old key as its current key can write rows while
    // this runs. Each pass picks up what the pass before it missed; the count that is left
    // after the last pass is reported, never hidden.
    private const int MaxPasses = 5;

    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        InstanceAuthorization auth,
        AppDbContext db,
        Bootstrap bootstrap,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var userId = await auth.RequireAdminAsync(user, ct);
        var keys = bootstrap.Keys;
        var connectionString = configuration.GetConnectionString("DefaultConnection")!;

        var missing = (await KeyInventory.ReadAsync(connectionString, ct)).Where(u => !keys.IsLoaded(u.MasterKeyId)).ToList();
        if (missing.Count > 0)
            return Results.Conflict(new ErrorResponse("key_unavailable", "Key rotation did not run",
                $"Rows are under a master key that is not loaded: {MasterKeyGuard.Describe(missing)}. Nothing was changed. " +
                "Stop every instance that uses another master key, start the app with that key as Vellum__PreviousMasterKey and rotate again."));

        var rewrapped = 0;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var moved = await RotateSecretsAsync(db, keys, ct) + await RotateDataProtectionKeysAsync(db, keys, ct);
            rewrapped += moved;
            if (moved == 0)
                break;
        }

        var left = (await KeyInventory.ReadAsync(connectionString, ct)).Where(u => !keys.IsCurrent(u.MasterKeyId)).ToList();
        var remaining = (int)left.Sum(u => u.Rows);
        await AuditLog.AppendAsync(db, null, userId, AuditLog.KeysRotated,
            $"master-key:{keys.Current.Id} rewrapped:{rewrapped} remaining:{remaining}", ct);

        return Results.Ok(new RotateKeysResponse(rewrapped, remaining, remaining == 0
            ? "Every stored key is under the current master key. You can remove the previous master key and restart."
            : $"{remaining} stored keys are still under another master key ({MasterKeyGuard.Describe(left)}). Do not remove the previous master key. " +
              "Stop every instance that runs with the old key as its current key, then rotate again. " +
              "A row that stays after that does not unwrap with its key; restore it from a backup or delete it."));
    }

    /// <summary>
    /// One pass over the secrets under another key, in batches. Each batch locks its rows,
    /// reads them under the lock and writes them in the same transaction, so a write of a
    /// secret that runs beside it happens wholly before or wholly after: the batch never
    /// combines its own wrapped key with another writer's ciphertext.
    /// </summary>
    private static async Task<int> RotateSecretsAsync(AppDbContext db, MasterKeyRing keys, CancellationToken ct)
    {
        var moved = 0;
        var current = keys.Current.Id;
        var (afterWorkspace, afterName) = (Guid.Empty, "");
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var batch = await db.Secrets.FromSql($"""
                SELECT * FROM settings.secrets
                WHERE master_key_id <> {current} AND (workspace_id, name) > ({afterWorkspace}, {afterName})
                ORDER BY workspace_id, name
                LIMIT {BatchSize}
                FOR UPDATE
                """).ToListAsync(ct);
            if (batch.Count == 0)
                return moved;

            foreach (var row in batch)
            {
                try
                {
                    SecretEnvelope.Rotate(keys, row);
                    moved++;
                }
                catch (Exception ex) when (ex is CryptographicException or MasterKeyUnavailableException)
                {
                    // The row does not unwrap. It stays as it is and counts as remaining.
                    db.Entry(row).State = EntityState.Unchanged;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            (afterWorkspace, afterName) = (batch[^1].WorkspaceId, batch[^1].Name);
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<int> RotateDataProtectionKeysAsync(AppDbContext db, MasterKeyRing keys, CancellationToken ct)
    {
        var moved = 0;
        var after = 0;
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var batch = await db.DataProtectionKeys.FromSql($"""
                SELECT * FROM settings.data_protection_keys WHERE id > {after} ORDER BY id LIMIT {BatchSize} FOR UPDATE
                """).ToListAsync(ct);
            if (batch.Count == 0)
                return moved;

            foreach (var row in batch)
            {
                try
                {
                    if (DataProtectionEnvelope.Rewrap(keys, row.Xml) is not { } xml)
                        continue;
                    row.Xml = xml;
                    moved++;
                }
                catch (Exception ex) when (ex is CryptographicException or MasterKeyUnavailableException or FormatException)
                {
                    // The row does not unwrap. It stays as it is and counts as remaining.
                    db.Entry(row).State = EntityState.Unchanged;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            after = batch[^1].Id;
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>
/// <paramref name="MasterKeysInUse"/> lists every master key that rows are encrypted under,
/// with row counts. After a rotation it holds the current key only.
/// </summary>
public sealed record DiagnosticsResponse(
    string Profile,
    string Version,
    DatabaseDiagnostics Database,
    IReadOnlyList<ModuleMigrations> Migrations,
    EventStoreDiagnostics EventStore,
    string MasterKeyId,
    bool PreviousMasterKeyLoaded,
    IReadOnlyList<MasterKeyInUse> MasterKeysInUse,
    TelemetryDiagnostics Telemetry,
    GitHubEnvironmentDiagnostics GitHubEnvironmentToken);

/// <summary>
/// The <c>GH_TOKEN</c> of the instance: <c>in_use</c> (a local instance uses it for the
/// workspaces its admin owns), <c>ignored_hosted</c> or <c>not_set</c>. The token is never in it.
/// </summary>
public sealed record GitHubEnvironmentDiagnostics(string State);

/// <summary>
/// Whether this instance exports traces, metrics and logs. <paramref name="EndpointHost"/> is
/// the host name of the collector and nothing else of its address. The export is set with the
/// environment variable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and needs a restart.
/// </summary>
public sealed record TelemetryDiagnostics(bool Enabled, string? EndpointHost);

public sealed record MasterKeyInUse(string MasterKeyId, long Secrets, long DataProtectionKeys, bool IsCurrent, bool IsLoaded);
public sealed record DatabaseDiagnostics(bool Reachable);
public sealed record ModuleMigrations(string Module, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);
public sealed record EventStoreDiagnostics(bool Reachable, long? Head, IReadOnlyList<EventStoreConsumer> Consumers);
public sealed record EventStoreConsumer(string Name, string Mode, string Status, long Lag);

public static class GetDiagnostics
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        InstanceAuthorization auth,
        Bootstrap bootstrap,
        IConfiguration configuration,
        AppDbContext db,
        IEventStoreAdmin eventStore,
        TelemetryState telemetry,
        Vellum.Modules.GitHub.EnvironmentGitHubToken gitHubToken,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        await auth.RequireAdminAsync(user, ct);
        var log = loggers.CreateLogger(typeof(GetDiagnostics));

        // The admin check above read the database, so it is reachable here. A failure in a
        // part below is reported for that part and logged; the response carries no detail.
        var migrations = new List<ModuleMigrations>();
        var keysInUse = new List<MasterKeyInUse>();
        var databaseReachable = true;
        try
        {
            var known = db.Database.GetMigrations().ToList();
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Intersect(known).ToList();
            migrations.Add(new ModuleMigrations(DatabaseMigrations.HistoryName, applied, known.Except(applied).ToList()));

            foreach (var usage in await KeyInventory.ReadAsync(configuration.GetConnectionString("DefaultConnection")!, ct))
                keysInUse.Add(new MasterKeyInUse(usage.MasterKeyId, usage.Secrets, usage.DataProtectionKeys,
                    bootstrap.Keys.IsCurrent(usage.MasterKeyId), bootstrap.Keys.IsLoaded(usage.MasterKeyId)));
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            log.LogError(ex, "Diagnostics could not read the applied migrations or the keys in use");
            databaseReachable = false;
        }

        EventStoreDiagnostics store;
        try
        {
            var status = await eventStore.GetStatusAsync(ct);
            store = new EventStoreDiagnostics(true, status.Head, status.Consumers
                .Select(c => new EventStoreConsumer(
                    c.Name, c.Mode.ToString().ToLowerInvariant(), c.Status.ToString().ToLowerInvariant(), c.Lag))
                .ToList());
        }
        catch (Exception ex) when (ex is NpgsqlException or DeedboxException or TimeoutException)
        {
            log.LogError(ex, "Diagnostics could not read the event store status");
            store = new EventStoreDiagnostics(false, null, []);
        }

        return Results.Ok(new DiagnosticsResponse(
            bootstrap.ProfileName,
            GetInstance.Version,
            new DatabaseDiagnostics(databaseReachable),
            migrations,
            store,
            bootstrap.Keys.Current.Id,
            bootstrap.Keys.HasPrevious,
            keysInUse,
            new TelemetryDiagnostics(telemetry.Enabled, telemetry.EndpointHost),
            new GitHubEnvironmentDiagnostics(gitHubToken.State)));
    }
}

/// <summary>Readiness: the app can serve a request only when it reaches its database.</summary>
internal sealed class DatabaseHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy(exception: ex);
        }
    }
}

/// <summary>
/// <c>dotnet Vellum.dll health</c>: asks the running app on this machine for <c>/health</c>.
/// The container health check runs it, because the runtime image has no HTTP client program.
/// </summary>
public static class HealthCommand
{
    /// <returns>The process exit code: 0 when the app answers 200, otherwise 1.</returns>
    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter error)
    {
        var port = (configuration["HTTP_PORTS"] ?? "8080").Split(';', StringSplitOptions.TrimEntries)[0];
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");

        // Host filtering refuses a name that is not in AllowedHosts, so the probe uses one that is.
        var allowed = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0 && !allowed[0].Contains('*'))
            request.Headers.Host = allowed[0];

        try
        {
            using var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return 0;
            await error.WriteLineAsync($"/health answered {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await error.WriteLineAsync($"/health did not answer: {ex.Message}");
            return 1;
        }
    }
}
