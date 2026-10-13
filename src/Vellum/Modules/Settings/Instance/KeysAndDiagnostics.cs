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

namespace Vellum.Modules.Settings.Instance;

public sealed record RotateKeysResponse(int Rewrapped);

/// <summary>
/// Moves every data key under the current master key. The operator starts the app with the
/// new key as <c>Vellum__MasterKey</c> and the old one as <c>Vellum__PreviousMasterKey</c>,
/// calls this, and then removes the previous key.
/// </summary>
public static class RotateKeys
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        InstanceAuthorization auth,
        SettingsDbContext db,
        Bootstrap bootstrap,
        CancellationToken ct)
    {
        var userId = await auth.RequireAdminAsync(user, ct);
        var keys = bootstrap.Keys;
        var rewrapped = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var secrets = await db.Secrets.Where(s => s.MasterKeyId != keys.Current.Id).ToListAsync(ct);
            foreach (var secret in secrets)
            {
                SecretEnvelope.Rewrap(keys, secret);
                rewrapped++;
            }

            foreach (var key in await db.DataProtectionKeys.ToListAsync(ct))
            {
                if (DataProtectionEnvelope.Rewrap(keys, key.Xml) is not { } xml)
                    continue;
                key.Xml = xml;
                rewrapped++;
            }
        }
        catch (Exception ex) when (ex is MasterKeyUnavailableException or CryptographicException or FormatException)
        {
            // Nothing is saved: a partial rotation would hide which rows still need the old key.
            var detail = ex is MasterKeyUnavailableException missing
                ? $"A stored key is wrapped with master key {missing.MasterKeyId}, which is not loaded. " +
                  "Start the app with that key as Vellum__PreviousMasterKey and rotate again."
                : "A stored key does not unwrap with its master key. Nothing was changed.";
            return Results.Conflict(new ErrorResponse("key_unavailable", "Key rotation did not run", detail));
        }

        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, null, userId, AuditLog.KeysRotated, $"master-key:{keys.Current.Id} rewrapped:{rewrapped}", ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new RotateKeysResponse(rewrapped));
    }
}

public sealed record DiagnosticsResponse(
    string Profile,
    string Version,
    DatabaseDiagnostics Database,
    IReadOnlyList<ModuleMigrations> Migrations,
    EventStoreDiagnostics EventStore,
    string MasterKeyId,
    bool PreviousMasterKeyLoaded);

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
        IServiceProvider services,
        IEventStoreAdmin eventStore,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        await auth.RequireAdminAsync(user, ct);
        var log = loggers.CreateLogger(typeof(GetDiagnostics));

        // The admin check above read the database, so it is reachable here. A failure in a
        // part below is reported for that part and logged; the response carries no detail.
        var migrations = new List<ModuleMigrations>();
        var databaseReachable = true;
        try
        {
            foreach (var (module, contextType) in DatabaseMigrations.Modules)
            {
                var context = (DbContext)services.GetRequiredService(contextType);
                var known = context.Database.GetMigrations().ToList();
                var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).Intersect(known).ToList();
                migrations.Add(new ModuleMigrations(module, applied, known.Except(applied).ToList()));
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            log.LogError(ex, "Diagnostics could not read the applied migrations");
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
            bootstrap.Keys.HasPrevious));
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
