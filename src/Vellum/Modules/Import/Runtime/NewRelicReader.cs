using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refit;
using Vellum.Modules.Settings;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Import.Runtime;

/// <summary>
/// NerdGraph, New Relic's GraphQL API. Every call is a read: a query, never a mutation. The
/// address is one of the two fixed endpoints of <see cref="NewRelicRegions"/>; no setting and
/// no request names another.
/// </summary>
[Headers("Content-Type: application/json", "Accept: application/json", "User-Agent: Vellum")]
internal interface INewRelicApi
{
    [Post("/graphql")]
    Task<HttpResponseMessage> Query([Body] NerdGraphRequest request,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

internal sealed record NerdGraphRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("query")] string Query,
    [property: System.Text.Json.Serialization.JsonPropertyName("variables")] JsonObject Variables);

/// <summary>The two NerdGraph endpoints. A New Relic Source names a region, never an address.</summary>
public static class NewRelicRegions
{
    public const string Us = "us";
    public const string Eu = "eu";

    public static readonly Uri UsEndpoint = new("https://api.newrelic.com");
    public static readonly Uri EuEndpoint = new("https://api.eu.newrelic.com");

    public static bool IsRegion(string? value) => value is Us or Eu;

    public static Uri Endpoint(string region) => region == Eu ? EuEndpoint : UsEndpoint;

    /// <summary>The host a Source of the region is filed under: its citation names it.</summary>
    public static string Host(string region) => Endpoint(region).Host;
}

/// <summary>Why a NerdGraph read gave no value.</summary>
internal abstract record NewRelicFailure
{
    private NewRelicFailure() { }

    /// <summary>New Relic refused the key, or the key may not read the account.</summary>
    public sealed record Unauthorized : NewRelicFailure;
    /// <param name="RetryAt">When to try again, from <c>Retry-After</c>; null when New Relic gave none.</param>
    public sealed record RateLimited(DateTimeOffset? RetryAt) : NewRelicFailure;
    /// <param name="Reason"><c>network</c>, <c>timeout</c>, <c>server_error</c>, <c>unexpected_response</c>, <c>url_refused</c>, <c>invalid_setting</c>, <c>secret_unavailable</c> or <c>no_key</c>.</param>
    public sealed record Unavailable(string Reason) : NewRelicFailure;
}

internal sealed record NewRelicEntity(string Guid, string Name, string Domain, string Type, string EntityType);

/// <param name="Source">The GUID of the entity the relationship starts at.</param>
/// <param name="Type">New Relic's relationship type, such as <c>CALLS</c>.</param>
internal sealed record NewRelicRelationship(string Source, string Target, string Type, NewRelicEntity? SourceEntity, NewRelicEntity? TargetEntity);

/// <param name="Complete">False when a bound cut the read, or New Relic answered with data and errors: some of the account was not read.</param>
internal sealed record NewRelicAccountRead(IReadOnlyList<NewRelicEntity> Entities, IReadOnlyList<NewRelicRelationship> Relationships, bool Complete, IReadOnlyList<string> Notes);

/// <summary>
/// Reads the entities of one New Relic account and the relationships between them, through
/// NerdGraph at the fixed endpoint of the account's region, with the account's read-only User
/// API key from the secret store. It reads no metric, trace, log or attribute: the queries
/// below ask for the GUID, the name, the domain and the type of each entity, and for the type
/// and the two ends of each relationship.
/// </summary>
internal sealed class NewRelicReader(INewRelicApi api, SecretReader secrets, TimeProvider time)
{
    public const int MaxEntities = 500;
    public const int MaxRelationshipsPerEntity = 500;
    public const int MaxRelationships = 20_000;
    /// <summary>Pages of the entity search. 500 entities at 200 a page need 3; more pages mean the cursor does not advance.</summary>
    public const int MaxSearchPages = 10;
    /// <summary>Further pages of related entities, across all entities of one read.</summary>
    public const int MaxRelatedPages = 200;
    /// <summary>NerdGraph returns at most 25 entities for one <c>entities(guids:)</c> call.</summary>
    public const int EntitiesPerCall = 25;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The entity kinds a Source reads: APM applications, OpenTelemetry and external services, browser and mobile applications.</summary>
    public static readonly string[] DomainTypes = ["APM-APPLICATION", "EXT-SERVICE", "BROWSER-APPLICATION", "MOBILE-APPLICATION"];

    public const string EntitySearchQuery = """
        query($query: String!, $cursor: String) {
          actor {
            entitySearch(query: $query) {
              results(cursor: $cursor) {
                nextCursor
                entities { guid name domain type entityType }
              }
            }
          }
        }
        """;

    public const string RelatedEntitiesQuery = """
        query($guids: [EntityGuid]!) {
          actor {
            entities(guids: $guids) {
              guid
              relatedEntities {
                nextCursor
                results {
                  type
                  source { entity { guid name domain type entityType } }
                  target { entity { guid name domain type entityType } }
                }
              }
            }
          }
        }
        """;

    public const string RelatedEntitiesPageQuery = """
        query($guid: EntityGuid!, $cursor: String) {
          actor {
            entity(guid: $guid) {
              relatedEntities(cursor: $cursor) {
                nextCursor
                results {
                  type
                  source { entity { guid name domain type entityType } }
                  target { entity { guid name domain type entityType } }
                }
              }
            }
          }
        }
        """;

    public const string TestQuery = """
        query($accountId: Int!) {
          actor {
            account(id: $accountId) { id name }
          }
        }
        """;

    /// <summary>The search text for the entities of one account. The account is a number, so the text holds nothing a person typed.</summary>
    public static string SearchText(long accountId) =>
        string.Create(CultureInfo.InvariantCulture, $"accountId = {accountId} AND domainType IN ({string.Join(", ", DomainTypes.Select(t => $"'{t}'"))})");

    /// <summary>The key of a Source, from the secret store, for the fixed endpoint of its region.</summary>
    public async Task<(OutboundCredential? Key, NewRelicFailure? Failure)> KeyAsync(Guid workspaceId, Guid sourceId, string region, CancellationToken ct)
    {
        try
        {
            return await secrets.ReadAsync(workspaceId, SecretNames.NewRelicApiKey(sourceId, region), NewRelicRegions.Endpoint(region), ct) switch
            {
                SecretRead.Found found => (new OutboundCredential("API-Key", null, found.Value), null),
                _ => (null, new NewRelicFailure.Unavailable("no_key")),
            };
        }
        catch (SecretUnavailableException)
        {
            return (null, new NewRelicFailure.Unavailable("secret_unavailable"));
        }
    }

    /// <summary>One small query that says whether the key can read the account.</summary>
    public async Task<(string? AccountName, NewRelicFailure? Failure)> TestAsync(string region, OutboundCredential key, long accountId, CancellationToken ct)
    {
        var read = await QueryAsync(region, key, "test", TestQuery, new JsonObject { ["accountId"] = accountId }, ct);
        if (read.Failure is { } failure)
            return (null, failure);
        var account = read.Data?["actor"]?["account"];
        if (account is null || read.Errors > 0)
            return (null, new NewRelicFailure.Unauthorized());
        return (Clean(account["name"]) ?? "", null);
    }

    /// <summary>
    /// Every entity of the account (paged by cursor), then the relationships of each (25
    /// entities per call, each further page by its own cursor), within the bounds above.
    /// A rate limit or an auth failure ends the read with that failure; nothing read so far is
    /// returned, since a partial account read as a whole would mark facts stale.
    /// </summary>
    public async Task<(NewRelicAccountRead? Read, NewRelicFailure? Failure)> ReadAccountAsync(
        string region, OutboundCredential key, long accountId, CancellationToken ct)
    {
        var notes = new List<string>();
        var complete = true;
        var entities = new Dictionary<string, NewRelicEntity>(StringComparer.Ordinal);
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var searchPages = 0;
        do
        {
            var before = entities.Count;
            searchPages++;
            var page = await QueryAsync(region, key, "entity-search", EntitySearchQuery,
                new JsonObject { ["query"] = SearchText(accountId), ["cursor"] = cursor }, ct);
            if (page.Failure is { } failure)
                return (null, failure);
            if (page.Errors > 0)
            {
                complete = false;
                notes.Add("entity-search-errors");
            }
            var results = page.Data?["actor"]?["entitySearch"]?["results"];
            if (results is null)
                return page.Errors > 0 ? (null, new NewRelicFailure.Unauthorized()) : (null, new NewRelicFailure.Unavailable("unexpected_response"));
            foreach (var node in results["entities"] as JsonArray ?? [])
            {
                if (Entity(node) is not { } entity)
                    continue;
                if (entities.Count >= MaxEntities)
                {
                    complete = false;
                    notes.Add("limit:entities");
                    break;
                }
                entities.TryAdd(entity.Guid, entity);
            }
            cursor = Clean(results["nextCursor"], 2000);
            // A cursor that comes back, a page that adds nothing, or too many pages: the paging
            // does not advance. The read stops there and is partial.
            if (cursor is not null && entities.Count < MaxEntities
                && (entities.Count == before || !cursors.Add(cursor) || searchPages >= MaxSearchPages))
            {
                complete = false;
                notes.Add("limit:pages");
                cursor = null;
            }
        }
        while (cursor is not null && entities.Count < MaxEntities);
        if (cursor is not null)
        {
            complete = false;
            notes.Add("limit:entities");
        }

        var relationships = new List<NewRelicRelationship>();
        var more = new List<(string Guid, string Cursor)>();
        foreach (var chunk in entities.Keys.Order(StringComparer.Ordinal).Chunk(EntitiesPerCall))
        {
            var page = await QueryAsync(region, key, "related-entities", RelatedEntitiesQuery,
                new JsonObject { ["guids"] = new JsonArray([.. chunk.Select(g => (JsonNode)g)]) }, ct);
            if (page.Failure is { } failure)
                return (null, failure);
            if (page.Errors > 0)
            {
                complete = false;
                notes.Add("related-entities-errors");
            }
            foreach (var node in page.Data?["actor"]?["entities"] as JsonArray ?? [])
            {
                var guid = Clean(node?["guid"]);
                var related = node?["relatedEntities"];
                if (guid is null || related is null)
                    continue;
                AddRelationships(related["results"] as JsonArray, relationships, ref complete, notes);
                if (Clean(related["nextCursor"], 2000) is { } next)
                    more.Add((guid, next));
            }
        }

        var relatedPages = 0;
        foreach (var (guid, first) in more)
        {
            var next = first;
            var read = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal) { first };
            while (next is not null)
            {
                if (read >= MaxRelationshipsPerEntity || relationships.Count >= MaxRelationships)
                {
                    complete = false;
                    notes.Add("limit:relationships");
                    break;
                }
                if (++relatedPages > MaxRelatedPages)
                {
                    complete = false;
                    notes.Add("limit:pages");
                    break;
                }
                var page = await QueryAsync(region, key, "related-entities-page", RelatedEntitiesPageQuery,
                    new JsonObject { ["guid"] = guid, ["cursor"] = next }, ct);
                if (page.Failure is { } failure)
                    return (null, failure);
                if (page.Errors > 0)
                {
                    complete = false;
                    notes.Add("related-entities-errors");
                }
                var related = page.Data?["actor"]?["entity"]?["relatedEntities"];
                if (related is null)
                {
                    complete = false;
                    break;
                }
                var added = AddRelationships(related["results"] as JsonArray, relationships, ref complete, notes);
                read += added;
                next = Clean(related["nextCursor"], 2000);
                if (next is not null && (added == 0 || !seen.Add(next)))
                {
                    complete = false;
                    notes.Add("limit:pages");
                    break;
                }
            }
        }

        return (new NewRelicAccountRead([.. entities.Values], relationships, complete, [.. notes.Distinct()]), null);
    }

    private static int AddRelationships(JsonArray? results, List<NewRelicRelationship> into, ref bool complete, List<string> notes)
    {
        var added = 0;
        foreach (var edge in results ?? [])
        {
            if (into.Count >= MaxRelationships)
            {
                complete = false;
                notes.Add("limit:relationships");
                break;
            }
            var source = Entity(edge?["source"]?["entity"]);
            var target = Entity(edge?["target"]?["entity"]);
            if (Clean(edge?["type"], 100) is not { } type || source is null || target is null)
                continue;
            into.Add(new NewRelicRelationship(source.Guid, target.Guid, type, source, target));
            added++;
        }
        return added;
    }

    private static NewRelicEntity? Entity(JsonNode? node) =>
        Clean(node?["guid"], 200) is { } guid && Clean(node?["name"], 500) is { } name
            ? new NewRelicEntity(guid, name, Clean(node?["domain"], 50) ?? "", Clean(node?["type"], 100) ?? "", Clean(node?["entityType"], 100) ?? "")
            : null;

    private static string? Clean(JsonNode? node, int max = 300) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && OutboundText.Clean(text, max) is { Length: > 0 } clean ? clean : null;

    private sealed record Answer(JsonNode? Data, int Errors, NewRelicFailure? Failure);

    private async Task<Answer> QueryAsync(string region, OutboundCredential key, string operation, string query, JsonObject variables, CancellationToken ct)
    {
        var call = new OutboundCall("newrelic", operation, NewRelicRegions.Endpoint(region), AllowPrivateNetwork: false, RequireHttps: true, Timeout)
        {
            Credential = key,
            MaxResponseBytes = 16 * 1024 * 1024,
        };
        try
        {
            using var response = await OutboundSend.RunAsync(() => api.Query(new NerdGraphRequest(query, variables), call, ct));
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                return new Answer(null, 0, new NewRelicFailure.Unauthorized());
            if (status == 429)
                return new Answer(null, 0, new NewRelicFailure.RateLimited(RetryAt(response)));
            if (status >= 500)
                return new Answer(null, 0, new NewRelicFailure.Unavailable("server_error"));
            if (status != 200 || await OutboundBody.ReadElementAsync(response, ct) is not { ValueKind: JsonValueKind.Object } body)
                return new Answer(null, 0, new NewRelicFailure.Unavailable("unexpected_response"));

            var json = JsonNode.Parse(body.GetRawText())!;
            var errors = json["errors"] as JsonArray ?? [];
            // NerdGraph answers 200 with an error list. Its classes say what kind of failure it is.
            var classes = errors.Select(e => Clean(e?["extensions"]?["errorClass"], 100) ?? Clean(e?["message"], 300) ?? "").ToList();
            if (classes.Any(c => c.Contains("TOO_MANY_REQUESTS", StringComparison.OrdinalIgnoreCase) || c.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase)))
                return new Answer(null, 0, new NewRelicFailure.RateLimited(RetryAt(response)));
            if (classes.Any(c => c.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase) || c.Contains("FORBIDDEN", StringComparison.OrdinalIgnoreCase)
                || c.Contains("ACCESS_DENIED", StringComparison.OrdinalIgnoreCase)))
                return new Answer(null, 0, new NewRelicFailure.Unauthorized());
            return new Answer(json["data"], errors.Count, null);
        }
        catch (OutboundException ex)
        {
            return new Answer(null, 0, new NewRelicFailure.Unavailable(ex.Failure switch
            {
                OutboundFailure.UrlRefused => "url_refused",
                OutboundFailure.InvalidRequest => "invalid_setting",
                OutboundFailure.Timeout => "timeout",
                _ => "network",
            }));
        }
        catch (JsonException)
        {
            return new Answer(null, 0, new NewRelicFailure.Unavailable("unexpected_response"));
        }
    }

    private DateTimeOffset? RetryAt(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } retry
            ? retry.Delta is { } delta ? time.GetUtcNow() + delta : retry.Date
            : null;

    /// <summary>A fixed sentence for a failure. It holds no text from New Relic and no key.</summary>
    public static ConnectionTestError Describe(NewRelicFailure failure) => failure switch
    {
        NewRelicFailure.Unauthorized => new("unauthorized", "New Relic refused the key, or the key cannot read this account. Use a User API key of a user who can read the account."),
        NewRelicFailure.RateLimited => new("rate_limited", "New Relic's rate limit is used up. Try again later."),
        NewRelicFailure.Unavailable { Reason: "no_key" } => new("no_key", "No API key is stored for this Source. Enter the key again."),
        NewRelicFailure.Unavailable { Reason: "secret_unavailable" } => new("secret_unavailable", "The stored key cannot be decrypted with the loaded master key. Enter the key again."),
        NewRelicFailure.Unavailable { Reason: "invalid_setting" } => new("invalid_setting", "The key holds a character that cannot be sent in a header. Nothing was sent. Enter the key again."),
        NewRelicFailure.Unavailable { Reason: "timeout" } => new("timeout", "New Relic did not answer in time."),
        NewRelicFailure.Unavailable { Reason: "network" or "url_refused" } => new("network", "No connection to New Relic could be made. This instance may have no network access."),
        NewRelicFailure.Unavailable { Reason: "server_error" } => new("server_error", "New Relic reported a server error. Try again later."),
        _ => new("unexpected_response", "The answer was not a NerdGraph answer."),
    };

    internal static bool IsNetwork(NewRelicFailure failure) => failure is NewRelicFailure.Unavailable { Reason: "network" or "timeout" or "url_refused" };
}
