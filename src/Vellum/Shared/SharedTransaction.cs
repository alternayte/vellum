using Deedbox;
using Npgsql;

namespace Vellum.Shared;

/// <summary>
/// Runs a unit of work that appends events in a transaction shared with the app's
/// DbContext (<c>UseDbContext</c>). Deedbox cannot replay such a transaction, so the app
/// runs the whole unit again in two cases:
/// <list type="bullet">
/// <item>DBX038: Deedbox did not count this instance as live and wrote nothing.</item>
/// <item>A deadlock: the database aborts the transaction and nothing is written.</item>
/// </list>
/// <para>
/// The lock rule for a unit. Deedbox takes the lock of a stream, then the gate of each inline
/// projection, then the store-wide position counter, and holds the counter until the commit.
/// A unit that needs several streams locks all of them first, in ascending stream-ID order,
/// with <c>Execute</c> and no events, and only then appends. After the first append it asks
/// for no stream lock, so it cannot wait for a writer that waits for the counter, and two
/// such units cannot wait for each other. A unit makes one append where one stream holds the
/// change; an import, which writes the model stream and one stream per schema, is the only
/// unit with more.
/// </para>
/// <para>
/// A cost sheet is a row, not a stream. Its lock is an advisory lock on its branch
/// (<c>CostBranches.LockSheetsAsync</c>). A unit that writes model streams takes the sheet
/// locks after its stream locks and before its first append, in ascending branch-ID order.
/// A write of one sheet takes no other lock while it holds that one, so it never waits for
/// a writer that waits for it.
/// </para>
/// <para>
/// A deadlock stays possible in three rare cases, which is why the rerun stays: a second
/// append takes the gate of another projection while a Deedbox cut-over or join holds that
/// gate and waits for the counter; two writers record a new event type for the first time;
/// two stream IDs share one 32-bit lock key.
/// </para>
/// Each attempt gets a new service scope. A DbContext counts its changes as saved after a
/// failed attempt, so an attempt must not reuse the DbContext of the one before it.
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
