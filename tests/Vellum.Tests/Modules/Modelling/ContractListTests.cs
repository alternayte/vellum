using System.Collections.Immutable;
using System.Net.Http.Json;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using Decision = Vellum.Shared.CommandResult<System.Collections.Generic.IReadOnlyList<object>>;

namespace Vellum.Tests.Modules.Modelling;

/// <summary>A model holds a bounded number of contracts of each kind.</summary>
public class ContractLimitTests
{
    private static readonly ElementId Orders = Guid.NewGuid();

    private static readonly ElementState Element =
        new(Orders, ElementKind.System, "Orders", null, null, null, ElementStatus.Current, null, [], Icon: null);

    /// <summary>A model with the most channels, operations and subscriptions it may hold.</summary>
    private static ModelState Full()
    {
        var channels = Enumerable.Range(0, ContractDecider.MaxPerKind)
            .Select(i => new ChannelState(Guid.NewGuid(), $"c{i}", ChannelKind.Topic)).ToList();
        return ModelState.Initial with
        {
            Elements = ImmutableDictionary<ElementId, ElementState>.Empty.Add(Orders, Element),
            Channels = channels.ToImmutableDictionary(c => c.Id),
            Operations = Enumerable.Range(0, ContractDecider.MaxPerKind)
                .Select(i => new OperationState(Guid.NewGuid(), Orders, $"o{i}", Transport.Http, null, null, 0, RetryPolicy.None))
                .ToImmutableDictionary(o => o.Id),
            Subscriptions = channels
                .Select(c => new SubscriptionState(Guid.NewGuid(), c.Id, Orders, RetryPolicy.None, null))
                .ToImmutableDictionary(s => s.Id),
        };
    }

    [Fact]
    public void A_full_model_refuses_one_more_contract_of_each_kind_and_states_the_limit()
    {
        var full = Full();
        var channel = full.Channels.Keys.First();

        Decision[] refused =
        [
            ContractDecider.AddOperation(full, new AddOperationCommand(Guid.NewGuid(), Orders, "one more", Transport.Http, null, null, 0, RetryPolicy.None)),
            ContractDecider.AddChannel(full, new AddChannelCommand(Guid.NewGuid(), "one-more", ChannelKind.Topic)),
            ContractDecider.AddSubscription(full, new AddSubscriptionCommand(Guid.NewGuid(), channel, Orders, RetryPolicy.None, null)),
        ];

        foreach (var decision in refused)
            Assert.Contains("5000", Assert.IsType<Decision.Conflict>(decision).Message);

        // A change or a removal of what is there still works.
        Assert.IsType<Decision.Success>(ContractDecider.UpdateChannel(full, new UpdateChannelCommand(channel, "renamed")));
    }

    [Fact]
    public void A_merge_that_would_put_main_over_the_limit_is_refused()
    {
        var refusal = MergedModel.Refusal(Full(), [new ModelEvent.ChannelAdded(Guid.NewGuid(), "from-the-draft", ChannelKind.Topic)]);

        Assert.NotNull(refusal);
        Assert.Contains("5000", refusal);
    }
}

public class ContractListTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string Url(Guid projectId, string path) => $"/api/projects/{projectId}/{path}";

    private static async Task<List<T>> AllPagesAsync<T>(HttpClient client, string url, int limit, int expectedPages)
    {
        var items = new List<T>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await client.GetFromJsonAsync<Page<T>>(
                $"{url}?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")))!;
            Assert.InRange(page.Items.Count, 1, limit);
            items.AddRange(page.Items);
            cursor = page.Cursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(expectedPages, pages);
        return items;
    }

    [Fact]
    public async Task Contract_lists_are_paged_in_a_stable_order()
    {
        var factory = fixture.Factory;
        using var client = await TestHost.SignInAsync(factory);
        var (_, projectId) = await TestHost.CreateProjectAsync(client);

        var owner = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "elements"), new { id = owner, kind = "system", name = "Orders" }));

        // Seven of each. Two channels share a name, so the ID decides their order.
        var channels = new List<(string Name, Guid Id)>();
        foreach (var name in new[] { "delta", "alpha", "charlie", "bravo", "alpha", "Zulu", "echo" })
        {
            var id = Guid.NewGuid();
            channels.Add((name, id));
            await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id, name, kind = "topic" }));
            await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "operations"), new { id = Guid.NewGuid(), ownerId = owner, name, transport = "http" }));
            await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "subscriptions"), new { id = Guid.NewGuid(), channelId = id, consumerId = owner }));
        }

        var expected = channels
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id)
            .Select(c => c.Id).ToList();

        var paged = await AllPagesAsync<ChannelDto>(client, Url(projectId, "channels"), limit: 3, expectedPages: 3);
        Assert.Equal(expected, paged.Select(c => c.Id));
        Assert.Equal(expected, (await AllPagesAsync<ChannelDto>(client, Url(projectId, "channels"), limit: 200, expectedPages: 1)).Select(c => c.Id));

        var operations = await AllPagesAsync<OperationDto>(client, Url(projectId, "operations"), limit: 2, expectedPages: 4);
        Assert.Equal(7, operations.Select(o => o.Id).Distinct().Count());
        Assert.Equal(operations.Select(o => o.Name).Order(StringComparer.Ordinal), operations.Select(o => o.Name));

        var subscriptions = await AllPagesAsync<SubscriptionDto>(client, Url(projectId, "subscriptions"), limit: 4, expectedPages: 2);
        Assert.Equal(7, subscriptions.Select(s => s.Id).Distinct().Count());
        Assert.Equal(subscriptions.Select(s => s.Id).Order(), subscriptions.Select(s => s.Id));

        // The default page holds 50 items and no request gets more than 200.
        for (var i = 0; i < 46; i++)
            await TestHost.Created(client.PostAsJsonAsync(Url(projectId, "channels"), new { id = Guid.NewGuid(), name = $"more-{i:D2}", kind = "queue" }));
        var first = (await client.GetFromJsonAsync<Page<ChannelDto>>(Url(projectId, "channels")))!;
        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.Cursor);
        var rest = (await client.GetFromJsonAsync<Page<ChannelDto>>(Url(projectId, $"channels?limit=100000&cursor={Uri.EscapeDataString(first.Cursor!)}")))!;
        Assert.Equal(3, rest.Items.Count);
        Assert.Null(rest.Cursor);
    }
}
