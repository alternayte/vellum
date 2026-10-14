using Vellum.Modules.Identity.AgentTokens;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;
using System.Security.Claims;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using System.Data;
using Vellum.Modules.Schemas;

namespace Vellum.Modules.Trace;

public static class ScenarioEndpoints
{
    public static WebApplication MapScenarioEndpoints(this WebApplication app)
    {
        var scenarios = app.MapGroup("/api/projects/{projectId}/scenarios")
            .RequireAuthorization()
            // A scenario is stored in at most ScenarioShape.MaxDocumentBytes. The request may be
            // larger, because an author's indentation is not stored.
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Scenarios");

        scenarios.MapGet("/", ReadScenarios.List).AllowAgent(AgentScope.Read)
            .Produces<Page<ScenarioSummaryDto>>();

        scenarios.MapPost("/", SaveScenario.Create)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>(StatusCodes.Status201Created);

        scenarios.MapGet("/{scenarioId}", ReadScenarios.Get).AllowAgent(AgentScope.Read)
            .Produces<ScenarioDto>();

        scenarios.MapPut("/{scenarioId}", SaveScenario.Update)
            .Accepts<Scenario>("application/json")
            .Produces<Scenario>();

        scenarios.MapDelete("/{scenarioId}", DeleteScenario.Handle)
            .Produces(StatusCodes.Status204NoContent);

        // A run reads a pinned model and writes nothing, so a Viewer may run, and so may an
        // agent token that only reads.
        scenarios.MapPost("/{scenarioId}/run", RunScenario.Handle).AllowAgent(AgentScope.Read)
            .Produces<RunScenarioResponse>();

        return app;
    }
}

public sealed record ScenarioSummaryDto(Guid Id, string Name);

/// <summary>
/// A scenario document as a read gives it: the document's own members and where it came from.
/// </summary>
/// <param name="Origin">Null for a scenario that only a person wrote. Else the accepted AI flow proposal.</param>
public sealed record ScenarioDto(
    ScenarioId Id, string Name, IReadOnlyList<Step> Steps, IReadOnlyList<Fault> Faults, IReadOnlyList<Assertion> Assertions,
    ScenarioOrigin? Origin)
{
    public static ScenarioDto From(Scenario document, ScenarioOrigin? origin) =>
        new(document.Id, document.Name, document.Steps, document.Faults, document.Assertions, origin);
}

public static class ReadScenarios
{
    public static async Task<IResult> List(
        Guid projectId,
        string? cursor, int? limit,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId);

        if (CursorEncoder.Decode(cursor) is { } decoded)
        {
            var (sortKey, afterId) = decoded;
            query = query.Where(s => string.Compare(s.Name, sortKey) > 0
                || (s.Name == sortKey && s.Id.CompareTo(afterId) > 0));
        }

        var items = await query
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Take(pageSize + 1)
            .Select(s => new ScenarioSummaryDto(s.Id, s.Name))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > pageSize)
        {
            items = items[..pageSize];
            nextCursor = CursorEncoder.Encode(items[^1].Name, items[^1].Id);
        }

        return Results.Ok(new Page<ScenarioSummaryDto>(items, nextCursor));
    }

    /// <summary>A scenario of another project reads as not found.</summary>
    public static async Task<IResult> Get(
        Guid projectId, Guid scenarioId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Viewer, ct);

        var state = await ScenarioStreams.LoadAsync(store, scenarioId, ct);
        return state.DocumentIn(projectId) is { } document
            ? Results.Ok(ScenarioDto.From(document, state.Origin))
            : Results.NotFound(new ErrorResponse("not_found", "Scenario not found"));
    }
}

/// <summary>
/// Create and replace. The body is the scenario document itself. A document with validation
/// issues is saved (SDD §8: blocking issues prevent Build Ready, not saving). A body that
/// cannot be read as a scenario document, or that breaks a limit of <see cref="ScenarioShape"/>,
/// is refused with 400, and one that is too large with 413.
/// </summary>
public static class SaveScenario
{
    // Any constant works; it keeps this lock apart from other advisory locks in the database.
    private const int ProjectLockSpace = 0x5343454E;

    private static readonly string[] StepTypes = ["call", "publish", "write", "parallel", "branch"];
    private static readonly string[] AssertionTypes = ["outcome", "event-count"];

    public static async Task<IResult> Create(
        Guid projectId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        if (document.Id.Value == Guid.Empty)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "A scenario needs an ID")]));
        }

        var result = await CreateAsync(http.RequestServices, projectId, document, ct);
        return result.ToCreatedResult($"/api/projects/{projectId}/scenarios/{document.Id}");
    }

    /// <summary>
    /// Creates a scenario from a document that is inside the limits of <see cref="ScenarioShape"/>.
    /// The HTTP create and the acceptance of an AI flow proposal both write through here.
    /// </summary>
    internal static Task<CommandResult<Scenario>> CreateAsync(
        IServiceProvider requestServices, Guid projectId, Scenario document, CancellationToken ct) =>
        SharedTransaction.RunAsync(requestServices, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var created = await CreateInTransactionAsync(
                db, services.GetRequiredService<IEventStore>().UseDbContext(db), projectId, document, null, ct);
            if (created is CommandResult<Scenario>.Success)
                await transaction.CommitAsync(ct);
            return created;
        }, ct);

    /// <summary>
    /// The create inside a transaction that the caller opened on <paramref name="db"/> and
    /// commits. <paramref name="pinned"/> is the event store bound to that context.
    /// </summary>
    /// <param name="origin">Set when the document is an accepted AI flow proposal.</param>
    internal static async Task<CommandResult<Scenario>> CreateInTransactionAsync(
        DbContext db, IEventStore pinned, Guid projectId, Scenario document, ScenarioOrigin? origin, CancellationToken ct)
    {
        // The count and the new row commit together, and creates of one project take turns on
        // an advisory lock, so two of them cannot both take the last place. No unit asks for
        // this lock while it holds the lock of a scenario stream, so it adds no wait cycle.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({ProjectLockSpace}, hashtext({projectId.ToString()}))", ct);

        var count = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM trace.scenarios WHERE project_id = {projectId}")
            .SingleAsync(ct);
        if (count >= ScenarioDecider.MaxPerProject)
        {
            return new CommandResult<Scenario>.Conflict(
                $"A project holds at most {ScenarioDecider.MaxPerProject} scenarios. Delete one before you add another.");
        }

        try
        {
            return await pinned.DecideInTransaction<ScenarioState, Scenario>(
                document.Id,
                state => ScenarioDecider.Create(state, projectId, document, origin),
                state => state.Document!,
                ct);
        }
        // The ID names a stream of another kind: a model branch or a schema.
        catch (DeedboxException e) when (e.Code == DeedboxError.StreamTypeMismatch)
        {
            return new CommandResult<Scenario>.Conflict(ScenarioDecider.IdInUse);
        }
    }

    public static async Task<IResult> Update(
        Guid projectId, Guid scenarioId,
        HttpContext http,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        IOptions<JsonOptions> json,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var (document, error) = await ReadAsync(http.Request, json.Value.SerializerOptions, ct);
        if (document is null)
            return error!;

        // The route names the scenario. A body may leave the ID out, but may not name another.
        if (document.Id.Value != Guid.Empty && document.Id.Value != scenarioId)
        {
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "The ID in the body differs from the ID in the path")]));
        }

        var replacement = document with { Id = scenarioId };
        var result = await ScenarioStreams.DecideAsync(
            store, scenarioId,
            state => ScenarioDecider.Update(state, projectId, replacement),
            state => state.Document!,
            new CommandResult<Scenario>.NotFound("Scenario not found"),
            ct);
        return result.ToHttpResult();
    }

    private static async Task<(Scenario? Document, IResult? Error)> ReadAsync(
        HttpRequest request, JsonSerializerOptions options, CancellationToken ct)
    {
        Scenario? document;
        try
        {
            using var raw = await JsonDocument.ParseAsync(
                request.Body, new JsonDocumentOptions { MaxDepth = options.MaxDepth }, ct);

            // The serializer reports a step without a known `type` as an abstract type it
            // cannot build. The author needs to know which step and which values are valid.
            if (TypeProblem(raw.RootElement) is { } problem)
                return (null, Malformed(problem));

            document = raw.Deserialize<Scenario>(options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, Malformed(ex.Message));
        }

        if (document is null)
            return (null, Malformed("The body is null."));

        // The decision applies the same rule. It is applied here first because a document that
        // breaks it may be too deep to write, and the size below is measured by writing it.
        if (ScenarioShape.Check(document) is { } shapeError)
        {
            return (null, Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError(shapeError.Field, shapeError.Message)])));
        }

        // Every save stores the whole document again, so the stored size is bounded, not only
        // the size of the request.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, options).Length;
        if (bytes > ScenarioShape.MaxDocumentBytes)
        {
            return (null, Results.Json(
                new ErrorResponse("scenario_too_large",
                    $"A scenario document holds at most {ScenarioShape.MaxDocumentBytes} bytes. This one has {bytes}."),
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        return (document, null);
    }

    /// <summary>The first step or assertion whose <c>type</c> is missing or unknown, or null.</summary>
    private static string? TypeProblem(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return Steps(Member(root, "steps"), "steps") ?? Assertions(Member(root, "assertions"));
    }

    private static string? Steps(JsonElement? list, string path)
    {
        if (list is not { ValueKind: JsonValueKind.Array } steps)
            return null;

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = $"{path}[{index++}]";
            if (step.ValueKind != JsonValueKind.Object)
                continue;
            if (TypeOf(step, at, "step", StepTypes, out var type) is { } problem)
                return problem;

            var nested = type switch
            {
                "call" => Steps(Member(step, "then"), $"{at}.then") ?? Steps(Member(step, "onError"), $"{at}.onError"),
                "branch" => Steps(Member(step, "then"), $"{at}.then") ?? Steps(Member(step, "else"), $"{at}.else"),
                "parallel" => Branches(Member(step, "branches"), at),
                "publish" => Handlers(Member(step, "handlers"), at),
                _ => null,
            };
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static string? Branches(JsonElement? branches, string at)
    {
        if (branches is not { ValueKind: JsonValueKind.Array } list)
            return null;

        var index = 0;
        foreach (var branch in list.EnumerateArray())
        {
            if (Steps(branch, $"{at}.branches[{index++}]") is { } problem)
                return problem;
        }

        return null;
    }

    private static string? Handlers(JsonElement? handlers, string at)
    {
        if (handlers is not { ValueKind: JsonValueKind.Object } map)
            return null;

        var index = 0;
        foreach (var handler in map.EnumerateObject())
        {
            if (Steps(handler.Value, $"{at}.handlers[{index++}]") is { } problem)
                return problem;
        }

        return null;
    }

    private static string? Assertions(JsonElement? list)
    {
        if (list is not { ValueKind: JsonValueKind.Array } assertions)
            return null;

        var index = 0;
        foreach (var assertion in assertions.EnumerateArray())
        {
            var at = $"assertions[{index++}]";
            if (assertion.ValueKind == JsonValueKind.Object
                && TypeOf(assertion, at, "assertion", AssertionTypes, out _) is { } problem)
                return problem;
        }

        return null;
    }

    private static string? TypeOf(JsonElement value, string at, string what, string[] types, out string? type)
    {
        type = null;
        var allowed = string.Join(", ", types);
        if (!value.TryGetProperty("type", out var given))
            return $"{at}: the {what} has no \"type\". Use one of: {allowed}.";

        if (given.ValueKind == JsonValueKind.String)
            type = types.FirstOrDefault(given.ValueEquals);

        return type is null
            ? $"{at}: the \"type\" of the {what} is not known. Use one of: {allowed}."
            : null;
    }

    /// <summary>A member by name in any letter case, as the serializer reads it.</summary>
    private static JsonElement? Member(JsonElement value, string name)
    {
        JsonElement? found = null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals(name) || string.Equals(SafeName(property), name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }

        return found;
    }

    private static string? SafeName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        // A name with half of a surrogate pair is no member the serializer knows.
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult Malformed(string detail) =>
        Results.BadRequest(new ErrorResponse("invalid_scenario", "The body is not a scenario document", detail));
}

/// <summary>
/// One playback. <paramref name="ModelVersion"/> is the version of the branch stream the run
/// was pinned to; the same scenario over the same version gives the same
/// <paramref name="Hash"/>. A scenario with <paramref name="Issues"/> has no events: it was
/// not played. <paramref name="Labels"/> names every ID an event can carry.
/// </summary>
public sealed record RunScenarioResponse(
    string EngineVersion,
    string Hash,
    long ModelVersion,
    IReadOnlyList<ScenarioIssue> Issues,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyDictionary<string, StepOutcome> Outcomes,
    IReadOnlyList<AssertionResult> Assertions,
    IReadOnlyDictionary<Guid, string> Labels);

public static class RunScenario
{
    public static Task<IResult> Handle(
        Guid projectId, Guid scenarioId, Guid? branchId,
        HttpContext http,
        IEventStore store,
        AppDbContext db,
        CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer, async (streamId, _) =>
        {
            // One snapshot: the scenario, the branch's model and the project's schemas are read
            // as of the same instant. The transaction only reads and is rolled back.
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var pinned = store.UseDbContext(db);

            var scenario = await ScenarioStreams.LoadAsync(pinned, scenarioId, ct);
            if (scenario.DocumentIn(projectId) is not { } document)
                return Results.NotFound(new ErrorResponse("not_found", "Scenario not found"));

            var (model, modelVersion) = await pinned.Load<ModelState>(StreamId.From(streamId), ct);

            // Only the project's own schemas count. A reference to anything else stays
            // unresolved, and the engine reports it as an issue.
            var referenced = ContractDecider.ReferencedSchemaIds(model);
            var schemas = await db.Schemas.AsNoTracking()
                .Where(s => s.ProjectId == projectId && referenced.Contains(s.Id))
                .Select(s => new { s.Id, s.Content })
                .ToListAsync(ct);

            var result = TraceEngine.Run(
                TraceModelBuilder.Build(model, schemas.ToDictionary(s => new SchemaId(s.Id), s => s.Content)),
                document);

            return Results.Ok(new RunScenarioResponse(
                result.EngineVersion, result.Hash, modelVersion,
                result.Issues, result.Events, result.Outcomes, result.Assertions,
                TraceModelBuilder.Labels(model)));
        }, ct);
}

public static class DeleteScenario
{
    public static async Task<IResult> Handle(
        Guid projectId, Guid scenarioId,
        ClaimsPrincipal user,
        WorkspaceAuthorizationService auth,
        IEventStore store,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var result = await ScenarioStreams.DecideAsync(
            store, scenarioId,
            state => ScenarioDecider.Delete(state, projectId),
            _ => true,
            new CommandResult<bool>.NotFound("Scenario not found"),
            ct);
        return result is CommandResult<bool>.Success ? Results.NoContent() : result.ToHttpResult();
    }
}
