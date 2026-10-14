using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Vellum.Modules.Workspaces;

public static class WorkspaceEndpoints
{
    public static WebApplication MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces")
            .RequireAuthorization()
            .WithTags("Workspaces");

        group.MapPost("/", CreateWorkspace.Handle);
        group.MapGet("/", ListWorkspaces.Handle);
        group.MapPatch("/{id}", UpdateWorkspace.Handle);
        group.MapDelete("/{id}", DeleteWorkspace.Handle);

        var members = group.MapGroup("/{workspaceId}/members").WithTags("Members");
        members.MapGet("/", ListMembers.Handle).Produces<MembersResponse>();
        members.MapPatch("/{memberUserId}", ChangeMemberRole.Handle).RequireHuman().Produces<MembershipDto>();
        members.MapDelete("/{memberUserId}", RemoveMember.Handle).RequireHuman();

        // A person joins a workspace only by accepting an invitation; nobody adds a member directly.
        var invitations = group.MapGroup("/{workspaceId}/invitations").WithTags("Members").RequireHuman();
        invitations.MapGet("/", ListInvitations.Handle).Produces<List<InvitationDto>>();
        invitations.MapPost("/", CreateInvitation.Handle).WithBodyLimit(RequestBodyLimit.Default)
            .Produces<IssuedInvitationDto>(StatusCodes.Status201Created)
            .Produces<IssuedInvitationDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<InvitationRate>);
        invitations.MapDelete("/{invitationId:guid}", RevokeInvitation.Handle);

        // The invitations of the signed-in person, in any workspace.
        var mine = app.MapGroup("/api/invitations").WithTags("Members").RequireHuman();
        mine.MapGet("/", MyInvitations.List).Produces<List<MyInvitationDto>>();
        // The secret of the link is in the body of each of these, never in the URL.
        var answers = mine.MapGroup("/{invitationId:guid}")
            .WithBodyLimit(RequestBodyLimit.Default)
            .AddEndpointFilter(CallerRate.Filter<InvitationResponseRate>);
        answers.MapPost("/open", MyInvitations.Open).Produces<MyInvitationDto>();
        answers.MapPost("/accept", MyInvitations.Accept).Produces<MembershipDto>();
        answers.MapPost("/decline", MyInvitations.Decline);

        var projects = group.MapGroup("/{workspaceId}/projects").WithTags("Projects");
        projects.MapPost("/", CreateProject.Handle);
        projects.MapGet("/", ListProjects.Handle).AllowAgent(AgentScope.Read);

        var projectsGroup = app.MapGroup("/api/projects")
            .RequireAuthorization()
            .WithTags("Projects");
        projectsGroup.MapGet("/{projectId}", GetProject.Handle).Produces<ProjectDetailDto>();
        projectsGroup.MapPatch("/{projectId}", UpdateProject.Handle);
        projectsGroup.MapDelete("/{projectId}", DeleteProject.Handle);

        return app;
    }
}

public sealed record CreateWorkspaceRequest(Guid Id, string Name);
public sealed record WorkspaceDto(Guid Id, string Name, string CreatedBy, DateTimeOffset CreatedAt);

public static class CreateWorkspace
{
    public static async Task<IResult> Handle(
        CreateWorkspaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();

        if (WorkspaceText.Refusal(request.Name, nameRequired: true) is { } refusal)
            return refusal;
        // The client chooses the ID, so a retry finds the same workspace. Without one, the
        // membership would point at no workspace.
        if (request.Id == Guid.Empty)
            return Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError("id", "An ID is required")]));

        // A retry by the creator gets the workspace back. Any other caller learns only that
        // the ID is taken, never the workspace.
        var existing = await db.Workspaces.FindAsync([request.Id], ct);
        if (existing is not null)
            return existing.CreatedBy == userId ? Results.Ok(ToDto(existing)) : IdInUse.Conflict();

        var workspace = new WorkspaceEntity
        {
            Id = request.Id,
            Name = request.Name,
            CreatedBy = userId
        };
        db.Workspaces.Add(workspace);

        db.Memberships.Add(new MembershipEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.Id,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            CreatedAt = time.GetUtcNow(),
        });

        // The workspace, its first owner and the audit entry commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspace.Id, userId, AuditLog.MemberAdded, $"user:{userId} role:owner", ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/workspaces/{workspace.Id}", ToDto(workspace));
    }

    private static WorkspaceDto ToDto(WorkspaceEntity w) => new(w.Id, w.Name, w.CreatedBy, w.CreatedAt);
}

/// <summary>What a workspace or a project may be called and described as.</summary>
internal static class WorkspaceText
{
    /// <summary>The 400 for a name or a description that breaks <see cref="TextRule"/>, or null.</summary>
    public static IResult? Refusal(string? name, bool nameRequired, string? description = null)
    {
        // A name that is given must not be blank, also where a missing name means "no change".
        var error = (nameRequired || name is not null) && string.IsNullOrWhiteSpace(name)
            ? new ValidationError("name", "Name is required")
            : TextRule.Line("name", name) ?? TextRule.Lines("description", description);

        return error is null
            ? null
            : Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
                Errors: [new FieldError(error.Field, error.Message)]));
    }
}

public static class ListWorkspaces
{
    public static async Task<IResult> Handle(
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken ct)
    {
        var userId = user.UserId();

        var workspaceIds = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync(ct);

        var workspaces = await db.Workspaces
            .AsNoTracking()
            .Where(w => workspaceIds.Contains(w.Id))
            .OrderBy(w => w.Name)
            .Select(w => new WorkspaceDto(w.Id, w.Name, w.CreatedBy, w.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(workspaces);
    }
}

public sealed record UpdateWorkspaceRequest(string? Name);

public static class UpdateWorkspace
{
    public static async Task<IResult> Handle(
        Guid id,
        UpdateWorkspaceRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        WorkspaceAuthorizationService auth,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(id, userId, WorkspaceRole.Owner, ct);

        if (WorkspaceText.Refusal(request.Name, nameRequired: false) is { } refusal)
            return refusal;

        var workspace = await db.Workspaces.FindAsync([id], ct);
        if (workspace is null)
            return Results.NotFound(new ErrorResponse("not_found", "Workspace not found"));

        if (request.Name is not null)
            workspace.Name = request.Name;

        workspace.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.CreatedBy, workspace.CreatedAt));
    }
}

/// <summary>
/// Deletes a workspace: the tenant boundary. Every project goes as a project delete does
/// (<see cref="IProjectRetention"/>), then the rows that belong to the workspace itself: its
/// settings and encrypted secrets, its price book, its agent tokens, its AI usage records and
/// its import blobs and scan charges.
/// <para>
/// The audit log stays. A database trigger keeps it append-only, so the entries of the
/// workspace remain and no request can list them once the workspace is gone; the delete itself
/// is written as an instance-level entry, which the instance admin reads.
/// </para>
/// </summary>
public static class DeleteWorkspace
{
    public const string AuditAction = "workspace.deleted";

    public static async Task<IResult> Handle(
        Guid id,
        ClaimsPrincipal user,
        AppDbContext db,
        [FromServices] IEnumerable<IProjectRetention> retentions,
        ILoggerFactory loggers,
        WorkspaceAuthorizationService auth,
        CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(id, userId, WorkspaceRole.Owner, ct);

        var workspace = await db.Workspaces.FindAsync([id], ct);
        if (workspace is null)
            return Results.NotFound(new ErrorResponse("not_found", "Workspace not found"));

        // The workspace, its members and its invitations go first, in one transaction with the
        // audit entry: every endpoint checks a membership, so no request reaches the data of
        // the workspace while it goes.
        var projectCount = await db.Projects.CountAsync(p => p.WorkspaceId == id, ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Memberships.Where(m => m.WorkspaceId == id).ExecuteDeleteAsync(ct);
            await db.Invitations.Where(i => i.WorkspaceId == id).ExecuteDeleteAsync(ct);
            await db.Workspaces.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
            await AuditLog.AppendAsync(db, null, userId, AuditAction, $"workspace:{id} projects:{projectCount}", ct);
            await transaction.CommitAsync(ct);
        }

        // The cleanup does not stop when the caller goes away. A project that a request created
        // while the members went is found by the next pass.
        var failures = new List<Exception>();
        while (true)
        {
            var projects = await db.Projects.AsNoTracking().Where(p => p.WorkspaceId == id).ToListAsync(CancellationToken.None);
            if (projects.Count == 0)
                break;
            foreach (var project in projects)
            {
                await db.Projects.Where(p => p.Id == project.Id).ExecuteDeleteAsync(CancellationToken.None);
                failures.AddRange(await DeleteProject.RunRetentionsAsync(
                    new DeletedProject(project.Id, id, project.StreamId, userId), retentions, loggers));
            }
        }

        try
        {
            await DeleteWorkspaceRowsAsync(db, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            loggers.CreateLogger(typeof(DeleteWorkspace)).LogError(ex, "The rows of workspace {WorkspaceId} were not deleted", id);
            failures.Add(ex);
        }

        if (failures.Count > 0)
            throw new AggregateException($"The data of workspace {id} was not deleted in full.", failures);
        return Results.NoContent();
    }

    /// <summary>The rows that name the workspace and no project. It runs after every project retention, which deletes the pins of the price book.</summary>
    private static async Task DeleteWorkspaceRowsAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        await db.WorkspaceSettings.Where(s => s.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.Secrets.Where(s => s.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.PriceEntries.Where(p => p.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.AgentTokens.Where(t => t.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.UsageRecords.Where(r => r.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.ScanCharges.Where(c => c.WorkspaceId == id).ExecuteDeleteAsync(ct);
        await db.Blobs.Where(b => b.WorkspaceId == id).ExecuteDeleteAsync(ct);
    }
}
