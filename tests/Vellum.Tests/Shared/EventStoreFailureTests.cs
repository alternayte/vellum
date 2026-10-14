using System.Text.Json;
using Deedbox;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

/// <summary>
/// An append that expects a version the stream is no longer at writes nothing, and the
/// caller gets the app's 409 conflict envelope.
/// </summary>
public class ConcurrencyConflictTests : IClassFixture<IntegrationFixture>
{
    private readonly IntegrationFixture _fixture;

    public ConcurrencyConflictTests(IntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Stale_append_is_refused_and_becomes_a_409_conflict_envelope()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var schemaId = Guid.NewGuid();
        var streamId = StreamId.From(schemaId);
        var created = new SchemaEvent.SchemaCreated(schemaId, "OrderPlaced", null, """{"type":"object"}""", 1, Guid.NewGuid());
        await store.Append(streamId, ExpectedVersion.NoStream, [created]);

        // A second writer that loaded the stream before the first append.
        var conflict = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.Append(streamId, ExpectedVersion.NoStream, [new SchemaEvent.SchemaDeleted(schemaId)]));

        var (state, version) = await store.Load<SchemaState>(streamId);
        Assert.Equal(1, version);
        Assert.False(state.Deleted);

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        Assert.True(await new ConcurrencyExceptionHandler().TryHandleAsync(http, conflict, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);
        Assert.Equal("conflict", body.RootElement.GetProperty("type").GetString());
    }
}

/// <summary>
/// An append in a transaction shared with the app's DbContexts can be refused without a
/// write. The unit of work then runs again with new scoped services.
/// </summary>
public class SharedTransactionTests
{
    [Fact]
    public async Task Refused_append_runs_again_in_a_new_scope_with_the_request_metadata()
    {
        using var provider = Services();
        using var request = provider.CreateScope();
        request.ServiceProvider.GetRequiredService<DeedboxContext>().Metadata =
            new EventMetadata { Actor = "user:42", CorrelationId = "request-1" };

        var attempts = new List<(Probe Probe, EventMetadata Metadata)>();
        var result = await SharedTransaction.RunAsync(request.ServiceProvider, services =>
        {
            attempts.Add((services.GetRequiredService<Probe>(), services.GetRequiredService<DeedboxContext>().Metadata));
            if (attempts.Count == 1)
                throw new DeedboxException(DeedboxError.InstanceEvicted, "DBX038: This instance was not counted as live.");
            return Task.FromResult("written");
        }, CancellationToken.None);

        Assert.Equal("written", result);
        Assert.Equal(2, attempts.Count);
        Assert.NotSame(attempts[0].Probe, attempts[1].Probe);
        Assert.All(attempts, attempt => Assert.Equal("user:42", attempt.Metadata.Actor));
        Assert.All(attempts, attempt => Assert.Equal("request-1", attempt.Metadata.CorrelationId));
    }

    [Fact]
    public async Task Any_other_failure_is_not_run_again()
    {
        using var provider = Services();
        using var request = provider.CreateScope();

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SharedTransaction.RunAsync<string>(request.ServiceProvider, _ =>
            {
                attempts++;
                throw new InvalidOperationException("boom");
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    private static ServiceProvider Services() => new ServiceCollection()
        .AddScoped<DeedboxContext>()
        .AddScoped<Probe>()
        .BuildServiceProvider();

    private sealed class Probe;
}
