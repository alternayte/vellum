using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// Contracts have no read-model rows. A list reads them from the branch's stored state, which
/// one load returns whole, so a page always holds every item and has no cursor.
/// </summary>
public static class ListContracts
{
    public static async Task<IResult> Operations(IEventStore store, Guid streamId, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        var items = state.Operations.Values
            .OrderBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id.Value)
            .Select(OperationDto.From)
            .ToList();
        return Results.Ok(new Page<OperationDto>(items, null));
    }

    public static async Task<IResult> Channels(IEventStore store, Guid streamId, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        var items = state.Channels.Values
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.Value)
            .Select(ChannelDto.From)
            .ToList();
        return Results.Ok(new Page<ChannelDto>(items, null));
    }

    public static async Task<IResult> Subscriptions(IEventStore store, Guid streamId, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        var items = state.Subscriptions.Values
            .OrderBy(s => s.Id.Value)
            .Select(SubscriptionDto.From)
            .ToList();
        return Results.Ok(new Page<SubscriptionDto>(items, null));
    }
}
