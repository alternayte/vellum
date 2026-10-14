using Deedbox;
using Vellum.Modules.Identity.AgentTokens;
using System.Security.Claims;
using Vellum.Shared;
using Vellum.Modules.Workspaces;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Drafts;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Schemas;

public static class SchemaEndpoints
{
    public static WebApplication MapSchemaEndpoints(this WebApplication app)
    {
        var schemas = app.MapGroup("/api/projects/{projectId}/schemas")
            .RequireAuthorization()
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Schemas");

        schemas.MapPost("/", async (
            Guid projectId,
            CreateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            IEventStore store,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await CreateSchema.HandleAsync(store, projectId, request, ct))
                .ToCreatedResult($"/api/projects/{projectId}/schemas/{request.Id}");
        });

        schemas.MapGet("/", async (
            Guid projectId,
            string? cursor, int? limit,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await ListSchemas.Handle(projectId, cursor, limit, db, ct);
        }).AllowAgent(AgentScope.Read);

        schemas.MapGet("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);
            return await GetSchema.Handle(projectId, schemaId, db, ct);
        }).AllowAgent(AgentScope.Read);

        schemas.MapPatch("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            UpdateSchemaRequest request,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            IEventStore store,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await UpdateSchema.HandleAsync(store, db, projectId, schemaId, request, ct))
                .ToHttpResult();
        });

        schemas.MapDelete("/{schemaId}", async (
            Guid projectId, Guid schemaId,
            ClaimsPrincipal user,
            WorkspaceAuthorizationService auth,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = user.UserId();
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
            return (await DeleteSchema.HandleAsync(http.RequestServices, projectId, schemaId, ct))
                .ToHttpResult();
        });

        return app;
    }
}

public sealed record SchemaDto(
    Guid Id, string Name, string? Description,
    string Content, int Version);

public sealed record CreateSchemaRequest(
    Guid Id, string Name, string? Description, string Content);

public static class CreateSchema
{
    public static Task<CommandResult<SchemaDto>> HandleAsync(
        IEventStore store, Guid projectId, CreateSchemaRequest request, CancellationToken ct)
    {
        var createCmd = new CreateSchemaCommand(
            request.Id, request.Name, request.Description,
            request.Content, projectId);

        return store.Decide<SchemaState, SchemaDto>(
            request.Id,
            state => SchemaDecider.Create(state, createCmd),
            state => ToDto(state.Schema!),
            ct);
    }

    internal static SchemaDto ToDto(SchemaStateEntry schema) => new(
        schema.Id, schema.Name, schema.Description, schema.Content, schema.Version);
}

public static class GetSchema
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid schemaId,
        AppDbContext db, CancellationToken ct)
    {
        var entity = await db.Schemas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);

        if (entity is null) return Results.NotFound(new ErrorResponse("not_found", "Schema not found"));

        return Results.Ok(new SchemaDto(
            entity.Id, entity.Name, entity.Description, entity.Content, entity.Version));
    }
}

public static class ListSchemas
{
    public static async Task<IResult> Handle(
        Guid projectId,
        string? cursor, int? limit,
        AppDbContext db, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId);

        var decoded = CursorEncoder.Decode(cursor);
        if (decoded is not null)
        {
            var (sortKey, afterId) = decoded.Value;
            query = query.Where(s => string.Compare(s.Name, sortKey) > 0
                || (s.Name == sortKey && s.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(pageSize + 1)
            .Select(s => new SchemaDto(s.Id, s.Name, s.Description, s.Content, s.Version))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            var last = items[^1];
            nextCursor = CursorEncoder.Encode(last.Name, last.Id);
        }

        return Results.Ok(new Page<SchemaDto>(items, nextCursor));
    }
}

public sealed record UpdateSchemaRequest(
    string? Name, string? Description, string? Content,
    bool SetDescription = false, bool SetContent = false);

public static class UpdateSchema
{
    public static async Task<CommandResult<SchemaDto>> HandleAsync(
        IEventStore store, AppDbContext db, Guid projectId, Guid schemaId, UpdateSchemaRequest request, CancellationToken ct)
    {
        // Verify schema belongs to project
        var belongsToProject = await db.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);
        if (!belongsToProject)
            return new CommandResult<SchemaDto>.NotFound("Schema not found");

        var updateCmd = new UpdateSchemaCommand(
            schemaId,
            Name: request.Name, SetName: request.Name is not null,
            Description: request.Description, SetDescription: request.SetDescription || request.Description is not null,
            Content: request.Content, SetContent: request.SetContent || request.Content is not null);

        return await store.Decide<SchemaState, SchemaDto>(
            schemaId,
            state => SchemaDecider.Update(state, updateCmd),
            state => CreateSchema.ToDto(state.Schema!),
            ct);
    }
}

/// <summary>
/// A schema that a live branch or a stored revision names stays. A revision is a fixed
/// snapshot that a variant can be forked from at any later time, so a schema it names can
/// never be deleted.
/// </summary>
public static class DeleteSchema
{
    public static async Task<CommandResult> HandleAsync(
        IServiceProvider requestServices, Guid projectId, Guid schemaId, CancellationToken ct)
    {
        // An ID that is no schema of this project is not looked up in the event store: it can
        // name a stream of another kind.
        var belongsToProject = await requestServices.GetRequiredService<AppDbContext>().Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);
        if (!belongsToProject)
            return new CommandResult.NotFound("Schema not found");

        // The locks, the search for references and the delete are one transaction. A unit that
        // fails with DBX038 or a deadlock wrote nothing and runs again.
        return await SharedTransaction.RunAsync<CommandResult>(requestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var store = services.GetRequiredService<IEventStore>().UseDbContext(db);

            // The stream lock first, then the row lock (see SchemaReferences). From here until
            // the commit no transaction can write a new reference to this schema.
            var locked = await store.Execute<SchemaState>(StreamId.From(schemaId), _ => [], ct);
            if (locked.State.Schema?.ProjectId != projectId)
                return new CommandResult.NotFound("Schema not found");

            var decision = SchemaDecider.Delete(locked.State, schemaId);
            if (decision is not CommandResult<IReadOnlyList<object>>.Success delete)
                return new CommandResult.NotFound("Schema not found");

            // The stream has the schema and the read model has no row for it: the read model
            // is being rebuilt. The row is the guard, so the delete waits for it.
            if (!await SchemaReferences.HoldForDeleteAsync(db, schemaId, ct))
                return new CommandResult.Conflict("The schema list is being rebuilt. Nothing was deleted; try again.");

            if (await ReferrersAsync(services, store, projectId, schemaId, ct) is { } refusal)
                return new CommandResult.Conflict(refusal);

            await store.Append(StreamId.From(schemaId), ExpectedVersion.Exact(locked.Version), delete.Value, ct);
            await transaction.CommitAsync(ct);
            return new CommandResult.Success();
        }, ct);
    }

    /// <summary>
    /// Why the schema must stay, or null. Each read sees what is committed when it runs, and
    /// every transaction that could add a reference has either committed or waits for the row.
    /// </summary>
    internal static async Task<string?> ReferrersAsync(
        IServiceProvider services, IEventStore store, Guid projectId, Guid schemaId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();

        // Operations and messages live in model streams, one per branch. The list of open
        // branches is read before their states: a merge moves a reference from a draft to main
        // in one commit, and the draft's stream keeps it, so one of the two reads finds it.
        var references = new List<string>();
        var branches = await ModelBranches.OpenAsync(
            projectId, db, ct);
        foreach (var (streamId, branch) in branches)
        {
            var (model, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
            references.AddRange(ContractDecider.ReferencesToSchema(model, schemaId).Select(r => $"{r} on {branch}"));
        }

        if (references.Count > 0)
        {
            return $"Schema cannot be deleted. It is referenced by {string.Join(", ", references)}. " +
                "Point them at another schema first.";
        }

        // A revision stores its model as JSON text, in which a GUID has one spelling. The text
        // search selects the few revisions that can name the schema; each is then read as a model.
        var idText = schemaId.ToString();
        var candidates = db.Revisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.State.Contains(idText))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new { r.Name, r.State })
            .AsAsyncEnumerable();
        await foreach (var revision in candidates.WithCancellation(ct))
        {
            var pinned = ContractDecider.ReferencesToSchema(CreateRevision.ReadState(revision.State), schemaId);
            if (pinned.Count > 0)
            {
                return $"Schema cannot be deleted. It is referenced by {string.Join(", ", pinned)} in revision '{revision.Name}'. " +
                    "A revision is a fixed snapshot, so a schema it names stays.";
            }
        }

        return null;
    }
}
