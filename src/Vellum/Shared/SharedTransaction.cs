using Deedbox;
using Npgsql;

namespace Vellum.Shared;

/// <summary>
/// Runs a unit of work that appends events in a transaction shared with the app's own
/// DbContexts (<c>UseDbContext</c>). Deedbox cannot replay such a transaction, so the app
/// runs the whole unit again in two cases:
/// <list type="bullet">
/// <item>DBX038: Deedbox did not count this instance as live and wrote nothing.</item>
/// <item>A deadlock: a transaction with more than one append can lose a lock cycle against a
/// concurrent append. The database aborts it and nothing is written.</item>
/// </list>
/// Each attempt gets a new service scope. A DbContext counts its changes as saved after a
/// failed attempt, so an attempt must not reuse the DbContexts of the one before it.
/// </summary>
public static class SharedTransaction
{
    private const int MaxAttempts = 4;

    public static async Task<T> RunAsync<T>(
        IServiceProvider requestServices,
        Func<IServiceProvider, Task<T>> work,
        CancellationToken ct)
    {
        var ambient = requestServices.GetRequiredService<DeedboxContext>();
        var scopes = requestServices.GetRequiredService<IServiceScopeFactory>();

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();

            // The attempt appends for the same tenant, actor and correlation as the request.
            var context = scope.ServiceProvider.GetRequiredService<DeedboxContext>();
            context.TenantId = ambient.TenantId;
            context.Metadata = ambient.Metadata;

            try
            {
                return await work(scope.ServiceProvider);
            }
            catch (Exception ex) when (attempt < MaxAttempts && WroteNothing(ex))
            {
                // After DBX038 Deedbox rejoins in the background, and a deadlock needs the
                // other transaction to finish. An immediate rerun would fail the same way.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << attempt)), ct);
            }
        }
    }

    private static bool WroteNothing(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DeedboxException { Code: DeedboxError.InstanceEvicted })
                return true;
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
                return true;
        }

        return false;
    }
}
