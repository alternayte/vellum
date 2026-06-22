using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Vellum.Kernel.EventStore;

public sealed class EventStore : IEventStore
{
    private readonly EventStoreDbContext _db;

    public EventStore(EventStoreDbContext db) => _db = db;

    public async Task<StreamSnapshot?> LoadAsync(Guid streamId, CancellationToken ct = default)
    {
        var stream = await _db.Streams
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StreamId == streamId, ct);

        if (stream is null) return null;
        return new StreamSnapshot(stream.Version, stream.State);
    }

    public async Task AppendAsync(
        Guid streamId,
        string streamType,
        int expectedVersion,
        JsonDocument newState,
        IReadOnlyList<NewEvent> events,
        CancellationToken ct = default)
    {
        var newVersion = expectedVersion + events.Count;

        var existing = await _db.Streams.FindAsync([streamId], ct);

        if (existing is null)
        {
            if (expectedVersion != 0)
                throw new ConcurrencyException(streamId, expectedVersion, -1);

            _db.Streams.Add(new StreamEntity
            {
                StreamId = streamId,
                StreamType = streamType,
                Version = newVersion,
                State = newState,
            });
        }
        else
        {
            if (existing.Version != expectedVersion)
                throw new ConcurrencyException(streamId, expectedVersion, existing.Version);

            existing.Version = newVersion;
            existing.State = newState;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        for (var i = 0; i < events.Count; i++)
        {
            _db.Events.Add(new EventEntity
            {
                StreamId = streamId,
                Version = expectedVersion + i + 1,
                EventType = events[i].EventType,
                Payload = events[i].Payload,
                Metadata = events[i].Metadata,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyException(streamId, expectedVersion, -1);
        }
    }
}
