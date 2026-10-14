using System.ComponentModel;
using System.Text.Json.Nodes;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Modelling;
using Vellum.Modules.Modelling.Contracts;
using Vellum.Modules.Modelling.Elements;
using Vellum.Modules.Modelling.Entities;
using Vellum.Modules.Modelling.Messages;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Modelling.Relationships;
using Vellum.Modules.Schemas;
using Vellum.Shared;

namespace Vellum.Modules.Mcp.Tools;

/// <summary>
/// The tools that read the main model of a project. They read the same rows and the same
/// model state as the HTTP lists, and write each item as the HTTP API writes it, so every
/// item has its truth label, its provenance and its stale mark.
/// </summary>
internal static class ModelTools
{
    private const string Cursor = "The nextCursor of the result before this one. Leave it out for the first page.";
    private const string Limit = "The most items to return: 1 to 200. Default 50.";

    public static async Task<CallToolResult> ListProjects(
        McpCall call,
        [Description(Cursor)] string? cursor = null,
        [Description(Limit)] int? limit = null)
    {
        var size = McpOutput.PageSize(limit);
        if (!Decode(cursor, out var after, out var badCursor))
            return badCursor;
        var (projects, refusal) = await call.Service<McpProjects>().VisibleAsync(call.User, AgentScope.Read, after, size + 1, call.Aborted);
        if (projects is null)
            return McpOutput.Error(AgentBoundary.AgentNotAllowedError, "This agent token cannot do this", refusal);

        var page = projects.Take(size).ToList();
        var items = new JsonArray([.. page.Select(p => new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["description"] = p.Description })]);
        var data = new JsonObject { ["workspaceId"] = AgentPrincipal.WorkspaceId(call.User), ["projects"] = items };
        var cut = McpOutput.Fit(data, items);
        var more = cut > 0 || projects.Count > size;
        return call.Ok(data,
            more && items.Count > 0 ? CursorEncoder.Encode(page[items.Count - 1].Name, page[items.Count - 1].Id) : null,
            Notice(cut, "projects"));
    }

    public static async Task<CallToolResult> GetModelSummary(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId)
    {
        var (db, branch, ct) = (call.Service<AppDbContext>(), call.Project.StreamId, call.Aborted);
        var elements = db.Elements.AsNoTracking().Where(e => e.ProjectId == projectId && e.Branch == branch);

        var byKind = await elements.GroupBy(e => e.Kind).Select(g => new { Kind = g.Key, Count = g.Count() }).OrderBy(g => g.Kind).ToListAsync(ct);
        var byFactStatus = await elements.GroupBy(e => e.FactStatus).Select(g => new { FactStatus = g.Key, Count = g.Count() }).OrderBy(g => g.FactStatus).ToListAsync(ct);

        return call.Ok(new JsonObject
        {
            ["projectName"] = call.Project.Name,
            ["totalElements"] = byKind.Sum(g => g.Count),
            ["elementsByKind"] = new JsonArray([.. byKind.Select(g => new JsonObject { ["kind"] = g.Kind, ["count"] = g.Count })]),
            ["elementsByFactStatus"] = new JsonArray([.. byFactStatus.Select(g => new JsonObject { ["factStatus"] = g.FactStatus, ["count"] = g.Count })]),
            ["staleElements"] = await elements.CountAsync(e => e.StaleSinceRevision != null, ct),
            ["relationshipCount"] = await db.Relationships.AsNoTracking().CountAsync(r => r.ProjectId == projectId && r.Branch == branch, ct),
            ["messageCount"] = await db.Messages.AsNoTracking().CountAsync(m => m.ProjectId == projectId && m.Branch == branch, ct),
            ["schemaCount"] = await call.Service<AppDbContext>().Schemas.AsNoTracking().CountAsync(s => s.ProjectId == projectId, ct),
        });
    }

    public static async Task<CallToolResult> FindElements(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("A part of the name. Letter case does not count.")] string? name = null,
        [Description("An element kind: actor, system, app, component or store.")] string? kind = null,
        [Description("A tag the element has.")] string? tag = null,
        [Description("An element status: current, planned or deprecated.")] string? status = null,
        [Description(Cursor)] string? cursor = null,
        [Description(Limit)] int? limit = null)
    {
        if (FilterError(("name", name), ("kind", kind), ("tag", tag), ("status", status)) is { } invalid)
            return invalid;

        var query = call.Service<AppDbContext>().Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == call.Project.StreamId);
        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(e => e.Name.ToLower().Contains(name.ToLower()));
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var wanted = kind.ToLowerInvariant();
            query = query.Where(e => e.Kind == wanted);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.ToLowerInvariant();
            query = query.Where(e => e.Status == wanted);
        }
        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(e => e.Tags.Contains(tag));
        if (!Decode(cursor, out var after, out var badCursor))
            return badCursor;
        if (after is var (afterName, afterId))
            query = query.Where(e => string.Compare(e.Name, afterName) > 0 || (e.Name == afterName && e.Id.CompareTo(afterId) > 0));

        var size = McpOutput.PageSize(limit);
        var rows = await query.OrderBy(e => e.Name).ThenBy(e => e.Id).Take(size + 1).ToListAsync(call.Aborted);
        var items = new JsonArray([.. rows.Take(size).Select(e => call.Node(Element(e)))]);
        var data = new JsonObject { ["count"] = 0, ["elements"] = items };
        var cut = McpOutput.Fit(data, items);
        data["count"] = items.Count;
        var more = cut > 0 || rows.Count > size;
        return call.Ok(data,
            more && items.Count > 0 ? CursorEncoder.Encode(rows[items.Count - 1].Name, rows[items.Count - 1].Id) : null,
            Notice(cut, "elements"));
    }

    public static async Task<CallToolResult> GetElement(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the element.")] Guid elementId)
    {
        var (db, branch, ct) = (call.Service<AppDbContext>(), call.Project.StreamId, call.Aborted);
        var element = await db.Elements.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Branch == branch && e.Id == elementId, ct);
        if (element is null)
            return McpOutput.Error("not_found", "Element not found");

        var data = new JsonObject
        {
            ["element"] = call.Node(Element(element)),
            ["outboundRelationships"] = await RelationshipsAsync(call, r => r.FromId == elementId),
            ["inboundRelationships"] = await RelationshipsAsync(call, r => r.ToId == elementId),
            ["producedMessages"] = await MessagesAsync(call, m => m.ProducerId == elementId),
            ["consumedMessages"] = await MessagesAsync(call, m => m.ConsumerIds.Contains(elementId)),
        };
        return Capped(call, data, "outboundRelationships", "inboundRelationships", "producedMessages", "consumedMessages");
    }

    public static async Task<CallToolResult> GetDependencies(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the element.")] Guid elementId) =>
        // A relationship from X to Y says that X uses Y. A consumer depends on the producer of what it consumes.
        Capped(call, new JsonObject
        {
            ["elementId"] = elementId,
            ["outboundRelationships"] = await RelationshipsAsync(call, r => r.FromId == elementId),
            ["consumedMessages"] = await MessagesAsync(call, m => m.ConsumerIds.Contains(elementId)),
        }, "outboundRelationships", "consumedMessages");

    public static async Task<CallToolResult> GetDependents(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("The ID of the element.")] Guid elementId) =>
        Capped(call, new JsonObject
        {
            ["elementId"] = elementId,
            ["inboundRelationships"] = await RelationshipsAsync(call, r => r.ToId == elementId),
            ["producedMessages"] = await MessagesAsync(call, m => m.ProducerId == elementId),
        }, "inboundRelationships", "producedMessages");

    public static async Task<CallToolResult> ListMessages(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description(Cursor)] string? cursor = null,
        [Description(Limit)] int? limit = null)
    {
        var (db, branch, ct) = (call.Service<AppDbContext>(), call.Project.StreamId, call.Aborted);
        var query = db.Messages.AsNoTracking().Where(m => m.ProjectId == projectId && m.Branch == branch);
        if (!Decode(cursor, out var after, out var badCursor))
            return badCursor;
        if (after is var (afterName, afterId))
            query = query.Where(m => string.Compare(m.Name, afterName) > 0 || (m.Name == afterName && m.Id.CompareTo(afterId) > 0));

        var size = McpOutput.PageSize(limit);
        var rows = await query.OrderBy(m => m.Name).ThenBy(m => m.Id).Take(size + 1).ToListAsync(ct);
        var page = rows.Take(size).ToList();

        var elementIds = page.Select(m => m.ProducerId).Concat(page.SelectMany(m => m.ConsumerIds)).Distinct().ToList();
        var elements = await db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == branch && elementIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Name, e.Kind })
            .ToDictionaryAsync(e => e.Id, ct);

        // An element that is gone keeps its ID, with no name.
        JsonObject Party(Guid id) => elements.TryGetValue(id, out var element)
            ? new JsonObject { ["id"] = id, ["name"] = element.Name, ["kind"] = element.Kind }
            : new JsonObject { ["id"] = id, ["name"] = null, ["kind"] = null };

        var items = new JsonArray([.. page.Select(m =>
        {
            var item = call.Node(Message(m))!.AsObject();
            item.Remove("producerId");
            item.Remove("consumerIds");
            item["producer"] = Party(m.ProducerId);
            item["consumers"] = new JsonArray([.. m.ConsumerIds.Select(Party)]);
            return item;
        })]);
        var data = new JsonObject { ["count"] = 0, ["messages"] = items };
        var cut = McpOutput.Fit(data, items);
        data["count"] = items.Count;
        var more = cut > 0 || rows.Count > size;
        return call.Ok(data,
            more && items.Count > 0 ? CursorEncoder.Encode(page[items.Count - 1].Name, page[items.Count - 1].Id) : null,
            Notice(cut, "messages"));
    }

    private static readonly string[] ContractKinds = ["operations", "channels", "subscriptions", "bindings", "messages", "schemas"];

    public static async Task<CallToolResult> GetContracts(
        McpCall call,
        [Description("The ID of the project.")] Guid projectId,
        [Description("Only the contracts of this element: the operations it owns, its subscriptions with their channels and bindings, the messages it produces or consumes, and the schemas those name.")] Guid? elementId = null,
        [Description("One kind: operations, channels, subscriptions, bindings, messages or schemas. Leave it out for every kind.")] string? kind = null,
        [Description("The nextCursor of one kind, from the result before this one. Give it together with kind.")] string? cursor = null,
        [Description("The most items of each kind: 1 to 200. Default 50.")] int? limit = null)
    {
        if (kind is not null && !ContractKinds.Contains(kind, StringComparer.Ordinal))
            return Invalid("kind", $"A kind is one of: {string.Join(", ", ContractKinds)}");
        if (cursor is not null && kind is null)
            return Invalid("cursor", "A cursor is of one kind. Give kind with it.");
        if (!Decode(cursor, out var after, out var badCursor))
            return badCursor;

        var (model, _) = await call.Service<IEventStore>().Load<ModelState>(StreamId.From(call.Project.StreamId), call.Aborted);
        if (elementId is { } id && !model.Elements.ContainsKey(new ElementId(id)))
            return McpOutput.Error("not_found", "Element not found");

        IEnumerable<OperationState> operations = model.Operations.Values;
        IEnumerable<ChannelState> channels = model.Channels.Values;
        IEnumerable<SubscriptionState> subscriptions = model.Subscriptions.Values;
        IEnumerable<BindingState> bindings = model.Bindings.Values;
        IEnumerable<MessageState> messages = model.Messages.Values;
        HashSet<Guid>? schemaIds = null;
        if (elementId is { } of)
        {
            operations = [.. operations.Where(o => o.OwnerId.Value == of)];
            subscriptions = [.. subscriptions.Where(s => s.ConsumerId.Value == of)];
            var used = subscriptions.SelectMany(s => new[] { s.ChannelId, s.DeadLetterChannelId ?? s.ChannelId }).ToHashSet();
            bindings = [.. bindings.Where(b => used.Contains(b.SourceChannelId) || used.Contains(b.TargetChannelId))];
            used.UnionWith(bindings.SelectMany(b => new[] { b.SourceChannelId, b.TargetChannelId }));
            channels = [.. channels.Where(c => used.Contains(c.Id))];
            messages = [.. messages.Where(m => m.ProducerId.Value == of || m.ConsumerIds.Any(c => c.Value == of))];
            schemaIds =
            [
                .. operations.SelectMany(o => new[] { o.RequestSchemaId, o.ResponseSchemaId }).OfType<SchemaId>().Select(s => s.Value),
                .. messages.Select(m => m.SchemaId).OfType<SchemaId>().Select(s => s.Value),
            ];
        }

        var schemas = (kind is null or "schemas")
            ? await call.Service<AppDbContext>().Schemas.AsNoTracking()
                .Where(s => s.ProjectId == projectId && (schemaIds == null || schemaIds.Contains(s.Id)))
                .Select(s => new SchemaRow(s.Id, s.Name, s.Description, s.Version))
                .ToListAsync(call.Aborted)
            : [];

        var size = McpOutput.PageSize(limit);
        var data = new JsonObject();
        var lists = new List<(JsonObject Section, JsonArray Items, Func<int, string?> CursorAt)>();

        void Section<T>(string name, IEnumerable<T> all, Func<T, string> sortKey, Func<T, Guid> idOf, Func<T, JsonNode?> write)
        {
            if (kind is not null && kind != name)
                return;
            var ordered = all.OrderBy(sortKey, StringComparer.Ordinal).ThenBy(idOf).ToList();
            var rest = after is var (key, afterId)
                ? ordered.Where(item => string.CompareOrdinal(sortKey(item), key) is var order && (order > 0 || (order == 0 && idOf(item).CompareTo(afterId) > 0))).ToList()
                : ordered;
            var page = rest.Take(size).ToList();
            var items = new JsonArray([.. page.Select(write)]);
            var section = new JsonObject
            {
                ["total"] = ordered.Count,
                ["items"] = items,
                ["nextCursor"] = rest.Count > size ? CursorEncoder.Encode(sortKey(page[^1]), idOf(page[^1])) : null,
            };
            data[name] = section;
            lists.Add((section, items, index => CursorEncoder.Encode(sortKey(page[index]), idOf(page[index]))));
        }

        Section("operations", operations, o => o.Name, o => o.Id.Value, o => call.Node(OperationDto.From(o)));
        Section("channels", channels, c => c.Name, c => c.Id.Value, c => call.Node(ChannelDto.From(c)));
        Section("subscriptions", subscriptions, _ => "", s => s.Id.Value, s => call.Node(SubscriptionDto.From(s)));
        Section("bindings", bindings, _ => "", b => b.Id.Value, b => call.Node(BindingDto.From(b)));
        Section("messages", messages, m => m.Name, m => m.Id.Value, m => call.Node(new MessageDto(
            m.Id, m.Name, m.Description, m.ProducerId, [.. m.ConsumerIds.Select(c => c.Value)], m.SchemaId, m.Tags,
            m.FactStatus.ToApiString(), ProvenanceMapper.ToDto(m.Provenance), StaleDto.From(m.Stale))));
        Section("schemas", schemas, s => s.Name, s => s.Id, s => new JsonObject
        {
            ["id"] = s.Id, ["name"] = s.Name, ["description"] = s.Description, ["version"] = s.Version, ["factStatus"] = "declared",
        });

        // Over the bound, the longest list gives up its last item, until the result fits.
        var cut = false;
        while (!McpOutput.Fits(data) && lists.Any(list => list.Items.Count > 0))
        {
            var (section, items, cursorAt) = lists.MaxBy(list => list.Items.Count);
            items.RemoveAt(items.Count - 1);
            section["nextCursor"] = items.Count > 0 ? cursorAt(items.Count - 1) : null;
            cut = true;
        }
        return call.Ok(data, notice: cut
            ? "The result was cut to fit the byte bound. A kind with a nextCursor has more items: call again with that kind and its cursor. A kind with total above zero and no items needs a smaller limit."
            : null);
    }

    private sealed record SchemaRow(Guid Id, string Name, string? Description, int Version);

    private static ElementDto Element(ElementEntity e) => new(
        e.Id, e.Kind, e.Name, e.Description, e.Technology, e.OwnerId, e.Status, e.ParentId, e.Tags, e.Icon, e.FactStatus,
        ProvenanceMapper.ToDto(e.ProvenanceSource, e.ProvenanceRevision, e.ProvenanceConfidence, e.ProvenanceAcceptedBy, e.ProvenanceAcceptedAt),
        StaleDto.From(e.StaleSinceRevision, e.StaleReason));

    private static MessageDto Message(MessageEntity m) => new(
        m.Id, m.Name, m.Description, m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags, m.FactStatus,
        ProvenanceMapper.ToDto(m.ProvenanceSource, m.ProvenanceRevision, m.ProvenanceConfidence, m.ProvenanceAcceptedBy, m.ProvenanceAcceptedAt),
        StaleDto.From(m.StaleSinceRevision, m.StaleReason));

    /// <summary>The relationships of the main model that match, by ID, one more than a result lists so that a longer list shows.</summary>
    private static async Task<JsonArray> RelationshipsAsync(McpCall call, System.Linq.Expressions.Expression<Func<RelationshipEntity, bool>> where)
    {
        var rows = await call.Service<AppDbContext>().Relationships.AsNoTracking()
            .Where(r => r.ProjectId == call.Project.Id && r.Branch == call.Project.StreamId)
            .Where(where)
            .OrderBy(r => r.Id).Take(McpOutput.MaxListed + 1)
            .ToListAsync(call.Aborted);
        return new JsonArray([.. rows.Select(r => call.Node(new RelationshipDto(
            r.Id, r.FromId, r.ToId, r.Label, r.Technology, r.MessageId, r.LineShape, r.Kind, r.FactStatus,
            ProvenanceMapper.ToDto(r.ProvenanceSource, r.ProvenanceRevision, r.ProvenanceConfidence, r.ProvenanceAcceptedBy, r.ProvenanceAcceptedAt),
            StaleDto.From(r.StaleSinceRevision, r.StaleReason))))]);
    }

    private static async Task<JsonArray> MessagesAsync(McpCall call, System.Linq.Expressions.Expression<Func<MessageEntity, bool>> where)
    {
        var rows = await call.Service<AppDbContext>().Messages.AsNoTracking()
            .Where(m => m.ProjectId == call.Project.Id && m.Branch == call.Project.StreamId)
            .Where(where)
            .OrderBy(m => m.Name).ThenBy(m => m.Id).Take(McpOutput.MaxListed + 1)
            .ToListAsync(call.Aborted);
        return new JsonArray([.. rows.Select(m => call.Node(Message(m)))]);
    }

    /// <summary>A result whose lists have no cursor: each keeps its first items, and the notice names a list that had more.</summary>
    internal static CallToolResult Capped(McpCall call, JsonObject data, params string[] lists)
    {
        var longer = new List<string>();
        foreach (var name in lists)
        {
            var items = data[name]!.AsArray();
            if (McpOutput.Cap(items) is not null)
                longer.Add(name);
        }
        foreach (var name in lists)
        {
            if (McpOutput.Fit(data, data[name]!.AsArray()) > 0 && !longer.Contains(name))
                longer.Add(name);
        }
        return call.Ok(data, notice: longer.Count > 0
            ? $"These lists have more items than the result holds: {string.Join(", ", longer)}. GET /api/projects/{call.Project.Id}/relationships (with fromId or toId) and GET /api/projects/{call.Project.Id}/messages give every item, in pages."
            : null);
    }

    private static string? Notice(int cut, string what) => cut > 0
        ? $"The result was cut to fit the byte bound: {cut} fewer {what} than the limit. nextCursor gets the next ones."
        : null;

    /// <summary>
    /// The cursor of a tool that reads the model itself. False for text that is not a cursor:
    /// the result is then the validation error of an HTTP list, never the first page.
    /// </summary>
    private static bool Decode(string? cursor, out (string SortKey, Guid Id)? after, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out CallToolResult? invalid)
    {
        (after, invalid) = (null, null);
        try
        {
            after = CursorEncoder.Decode(cursor);
            return true;
        }
        catch (InvalidCursorException)
        {
            invalid = Invalid(InvalidCursorException.Field, InvalidCursorException.Text);
            return false;
        }
    }

    internal static CallToolResult Invalid(string field, string message) => McpOutput.Error(
        "validation_error", "Validation failed",
        document: new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["field"] = field, ["message"] = message }) });

    /// <summary>A filter is one line of at most <see cref="TextRule.MaxName"/> characters, as a stored name is.</summary>
    private static CallToolResult? FilterError(params (string Field, string? Value)[] filters)
    {
        foreach (var (field, value) in filters)
        {
            if (TextRule.Line(field, value) is { } error)
                return Invalid(error.Field, error.Message);
        }
        return null;
    }
}
