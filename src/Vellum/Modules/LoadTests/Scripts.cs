using System.Security.Claims;
using System.Text.Json;
using Deedbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Stress;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.LoadTests;

public sealed record GenerateScriptRequest(Guid TargetId, Guid ProfileId);

/// <summary>No script was generated. <paramref name="Type"/> is a <see cref="K6Refusal.Code"/>.</summary>
public sealed record ScriptRefusedResponse(string Type, string Title, IReadOnlyList<string> Reasons);

/// <summary>
/// A generated script and what pins it. The same scenario version, model version, target and
/// profile version give the same <paramref name="Hash"/> and the same <paramref name="Content"/>.
/// </summary>
/// <param name="Command">The command that runs the script on the machine of the caller.</param>
/// <param name="CiWorkflow">A GitHub Actions workflow that runs the script and keeps the result file.</param>
/// <param name="Content">The text of the script. Null in a list.</param>
public sealed record ScriptDto(
    Guid Id, string Hash, string FileName, string Generator,
    Guid ScenarioId, long ScenarioVersion, Guid ModelStreamId, long ModelVersion, Guid ProfileId, int ProfileVersion,
    Guid TargetId, string TargetName, string BaseUrl, string Environment, string AuthorisedBy, DateTimeOffset AuthorisedAt,
    DateTimeOffset CreatedAt, K6Plan Plan, string Command, string CiWorkflow, string? Content);

public static class Scripts
{
    public const int MaxScriptsPerProject = 300;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static K6Plan Plan(ScriptEntity row) => JsonSerializer.Deserialize<K6Plan>(row.Plan, Json)!;

    public static string FileName(string hash) => $"vellum-k6-{hash[..12]}.js";

    /// <summary>
    /// Generates the script for a stress profile and a target, and stores it. The server does
    /// not run k6 and sends no request to the target. A refusal stores nothing.
    /// </summary>
    public static Task<IResult> Generate(
        Guid projectId, Guid? branchId, GenerateScriptRequest request,
        HttpContext http, IEventStore store, AppDbContext db, TimeProvider time,
        CancellationToken ct) =>
        ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor, async (streamId, userId) =>
        {
            var profileRow = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProfileId && p.ProjectId == projectId, ct);
            if (profileRow is null)
                return Targets.NotFound("Stress profile");
            if (await Targets.LoadAsync(db, projectId, request.TargetId, ct) is not (var target, _))
                return Targets.NotFound("Load-test target");

            var profile = StressProfiles.Read(profileRow.Document);
            var world = await StressAccess.LoadAsync(store, db, projectId, streamId, profile.ScenarioId, ct);
            if (world.Scenario is null)
                return Refused(new K6Refusal("scenario-not-valid", "The scenario of this profile does not exist any more. Choose another scenario in the profile.", []));

            var (script, refusal) = K6ScriptGenerator.Generate(new K6Input(
                world.Scenario, world.ScenarioVersion, world.Model, streamId, world.ModelVersion,
                request.TargetId, target, profileRow.Id, profileRow.Version, profile.Arrivals));
            if (script is null)
                return Refused(refusal!);

            // The same inputs give the same hash. The row of the first request is the script.
            if (await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Hash == script.Hash, ct) is { } existing)
                return Results.Ok(Dto(existing, withContent: true));

            var authorisation = target.AuthorisationInForce!;
            var row = new ScriptEntity
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Hash = script.Hash,
                ScenarioId = profile.ScenarioId, ScenarioVersion = world.ScenarioVersion,
                ModelStreamId = streamId, ModelVersion = world.ModelVersion,
                ProfileId = profileRow.Id, ProfileVersion = profileRow.Version,
                TargetId = request.TargetId, TargetName = target.Name!, BaseUrl = authorisation.BaseUrl, Environment = authorisation.Environment,
                AuthorisedBy = authorisation.By, AuthorisedAt = authorisation.At,
                Content = script.Content, Plan = JsonSerializer.Serialize(script.Plan, Json),
                CreatedBy = userId, CreatedAt = time.GetUtcNow(),
            };

            // The count, the removal and the insert are one unit, and the writes of one project take turns: two
            // requests cannot both take the last place, and no result is imported for the script that makes room.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await Targets.LockProjectAsync(db.Database, projectId, ct);

            // A project keeps a bounded number of scripts. The oldest one that no result names makes room.
            if (await db.Scripts.CountAsync(s => s.ProjectId == projectId, ct) >= MaxScriptsPerProject)
            {
                var oldest = await db.Scripts.Where(s => s.ProjectId == projectId && !db.Results.Any(r => r.ScriptId == s.Id))
                    .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
                if (oldest is null)
                {
                    return Results.Conflict(new ErrorResponse("conflict",
                        $"A project holds at most {MaxScriptsPerProject} scripts, and each one has a result. Delete a result before you generate another script."));
                }
                try
                {
                    await db.Scripts.Where(s => s.Id == oldest).ExecuteDeleteAsync(ct);
                }
                // A writer that does not take the lock gave that script a result. Nothing is removed and nothing is stored.
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
                {
                    return Results.Conflict(new ErrorResponse("conflict",
                        "A result was imported for the script that would make room. Generate the script again."));
                }
            }

            db.Scripts.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            // Two requests for the same script at once: the other one stored it. The failed statement ended this transaction.
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await transaction.RollbackAsync(ct);
                var stored = await db.Scripts.AsNoTracking().FirstAsync(s => s.ProjectId == projectId && s.Hash == script.Hash, ct);
                return Results.Ok(Dto(stored, withContent: true));
            }
            return Results.Created($"/api/projects/{projectId}/load-tests/scripts/{row.Id}", Dto(row, withContent: true));
        }, ct);

    private static IResult Refused(K6Refusal refusal) =>
        Results.Json(new ScriptRefusedResponse(refusal.Code, refusal.Message, refusal.Reasons), statusCode: StatusCodes.Status422UnprocessableEntity);

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        // Without the text of the scripts: a list does not read them.
        var rows = await db.Scripts.AsNoTracking().Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Select(s => new ScriptEntity
            {
                Id = s.Id, ProjectId = s.ProjectId, Hash = s.Hash, ScenarioId = s.ScenarioId, ScenarioVersion = s.ScenarioVersion,
                ModelStreamId = s.ModelStreamId, ModelVersion = s.ModelVersion, ProfileId = s.ProfileId, ProfileVersion = s.ProfileVersion,
                TargetId = s.TargetId, TargetName = s.TargetName, BaseUrl = s.BaseUrl, Environment = s.Environment,
                AuthorisedBy = s.AuthorisedBy, AuthorisedAt = s.AuthorisedAt, Plan = s.Plan, CreatedBy = s.CreatedBy, CreatedAt = s.CreatedAt,
            })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => Dto(r, withContent: false)).ToList());
    }

    public static async Task<IResult> Get(
        Guid projectId, Guid scriptId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Viewer, ct);

        var row = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId, ct);
        return row is null ? Targets.NotFound("Script") : Results.Ok(Dto(row, withContent: true));
    }

    internal static ScriptDto Dto(ScriptEntity row, bool withContent)
    {
        var file = FileName(row.Hash);
        return new ScriptDto(
            row.Id, row.Hash, file, K6ScriptGenerator.Version,
            row.ScenarioId, row.ScenarioVersion, row.ModelStreamId, row.ModelVersion, row.ProfileId, row.ProfileVersion,
            row.TargetId, row.TargetName, row.BaseUrl, row.Environment, row.AuthorisedBy, row.AuthorisedAt, row.CreatedAt,
            Plan(row), Command(row.BaseUrl, file), CiWorkflow(row.BaseUrl, file), withContent ? row.Content : null);
    }

    /// <summary>The base URL in single quotes for a POSIX shell. A stored base URL holds no control character.</summary>
    private static string Command(string baseUrl, string file) =>
        $"k6 run -e VELLUM_BASE_URL='{baseUrl.Replace("'", "'\\''")}' {file}";

    /// <summary>
    /// The workflow reads the credential from a repository secret; the script holds none. The
    /// base URL is the authorised one, as a JSON string, which is also a YAML string.
    /// </summary>
    private static string CiWorkflow(string baseUrl, string file) => $$$"""
        name: Vellum k6 load test
        on:
          workflow_dispatch:
        jobs:
          k6:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: grafana/setup-k6-action@v1
              - name: Run the script that Vellum generated
                env:
                  VELLUM_BASE_URL: {{{JsonSerializer.Serialize(baseUrl)}}}
                  VELLUM_AUTHORIZATION: ${{ secrets.VELLUM_AUTHORIZATION }}
                run: k6 run load-tests/{{{file}}}
              - name: Keep the result file
                if: always()
                uses: actions/upload-artifact@v4
                with:
                  name: vellum-k6-result
                  path: vellum-k6-result.json
                  if-no-files-found: error

        """;
}
