using Deedbox;
using Vellum.Modules.Modelling.Model;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

/// <summary>
/// Contracts have no read-model rows. A list reads them from the branch's stored state, which
/// <see cref="ContractDecider.MaxPerKind"/> bounds, and returns one page of it. The order is
/// by name, then by ID; subscriptions have no name and are ordered by ID alone. Text is
/// compared by code point, so the order does not depend on the server's culture.
/// </summary>
public static class ListContracts
{
    public static async Task<IResult> Operations(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Operations.Values, o => o.Name, o => o.Id.Value, OperationDto.From, cursor, limit));
    }

    public static async Task<IResult> Channels(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Channels.Values, c => c.Name, c => c.Id.Value, ChannelDto.From, cursor, limit));
    }

    public static async Task<IResult> Subscriptions(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Subscriptions.Values, _ => "", s => s.Id.Value, SubscriptionDto.From, cursor, limit));
    }

    /// <summary>The same paging as the lists that read rows: 50 items by default, 200 at most.</summary>
    private static Page<TDto> Page<T, TDto>(
        IEnumerable<T> items, Func<T, string> sortKey, Func<T, Guid> id, Func<T, TDto> toDto,
        string? cursor, int? limit)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        if (CursorEncoder.Decode(cursor) is { } after)
        {
            items = items.Where(item =>
            {
                var order = string.CompareOrdinal(sortKey(item), after.SortKey);
                return order > 0 || (order == 0 && id(item).CompareTo(after.Id) > 0);
            });
        }

        var page = items
            .OrderBy(sortKey, StringComparer.Ordinal).ThenBy(id)
            .Take(pageSize + 1)
            .ToList();

        string? next = null;
        if (page.Count > pageSize)
        {
            page.RemoveAt(pageSize);
            next = CursorEncoder.Encode(sortKey(page[^1]), id(page[^1]));
        }

        return new Page<TDto>(page.Select(toDto).ToList(), next);
    }
}
