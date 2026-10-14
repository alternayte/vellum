using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Modules.Workspaces.Entities;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

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
            Role = nameof(WorkspaceRole.Owner),
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
