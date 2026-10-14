using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vellum.Modules.Build;
using Vellum.Modules.Promises;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Sharing;

/// <param name="Scope"><c>project</c> or <c>view</c>.</param>
/// <param name="ViewId">The view of a view link.</param>
/// <param name="Mode"><c>live</c> or <c>pinned</c>.</param>
/// <param name="RevisionId">The named revision of a pinned link.</param>
/// <param name="Password">Empty or null: no password. The workspace policy can require one.</param>
/// <param name="ExpiresAt">Null: the link does not expire.</param>
public sealed record CreateShareLinkRequest(
    string Scope, Guid? ViewId, string Mode, Guid? RevisionId, string? Label,
    bool ShowCosts, bool ShowPromises, string? Password, DateTimeOffset? ExpiresAt);

/// <summary>A field that is null stays as it is.</summary>
/// <param name="Password">A new password. The old one, and every unlock made with it, stops working.</param>
/// <param name="RemovePassword">True makes the link public, when the workspace policy allows that.</param>
/// <param name="ClearExpiry">True removes the expiry.</param>
public sealed record ChangeShareLinkRequest(
    string? Label, bool? ShowCosts, bool? ShowPromises, string? Password, bool RemovePassword,
    DateTimeOffset? ExpiresAt, bool ClearExpiry);

/// <param name="Status"><c>active</c>, <c>expired</c>, <c>revoked</c> or <c>blocked</c> (the workspace policy does not allow it now).</param>
/// <param name="CanManage">True when the caller may change and revoke the link: its creator, or an Owner.</param>
/// <param name="LastAccessedAt">The last time anyone read the link, to the minute. No visitor is recorded.</param>
public sealed record ShareLinkDto(
    Guid Id, string Label, string Scope, Guid? ViewId, string? ViewName, string Mode,
    Guid? RevisionId, string? RevisionName, DateTimeOffset? RevisionCreatedAt,
    bool ShowCosts, bool ShowPromises, bool HasPassword, DateTimeOffset? ExpiresAt,
    string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastAccessedAt, string Status, bool CanManage);

/// <param name="Token">The secret of the link. It is shown here once and is not stored.</param>
/// <param name="Path">The page of the link, from the root of this Vellum.</param>
/// <param name="EmbedPath">The page to put in a frame, for a view link. Null for a project link.</param>
public sealed record ShareLinkCreatedDto(ShareLinkDto Link, string Token, string Path, string? EmbedPath);

public static class SharePassword
{
    public const int MinLength = 8;
    public const int MaxLength = 200;

    public static ValidationError? Check(string field, string password) =>
        password.Length is < MinLength or > MaxLength
            ? new ValidationError(field, $"Must be {MinLength} to {MaxLength} characters.")
            : null;
}

/// <summary>The audit entries of share links. A target names the link and what changed, never the token or the password.</summary>
public static class ShareAudit
{
    public const string Created = "share-link.created";
    public const string Changed = "share-link.changed";
    public const string Revoked = "share-link.revoked";

    public static string Describe(ShareLinkEntity link) => string.Create(CultureInfo.InvariantCulture,
        $"share-link:{link.Id} project:{link.ProjectId} scope:{link.Scope}{(link.ViewId is { } v ? $":{v}" : "")} mode:{link.Mode}" +
        $"{(link.RevisionId is { } r ? $":{r}" : "")} protection:{(link.PasswordHash is null ? "public" : "password")} " +
        $"expires:{(link.ExpiresAt is { } e ? e.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : "never")} " +
        $"costs:{(link.ShowCosts ? "on" : "off")} promises:{(link.ShowPromises ? "on" : "off")}");
}

/// <summary>
/// Share links of a project, for its members. An Editor or an Owner makes a link; its creator
/// or an Owner changes and revokes it. Every change is in the audit log of the workspace.
/// </summary>
public static class ShareLinks
{
    public const int MaxLinksPerProject = 200;

    public static async Task<IResult> List(
        Guid projectId, ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth,
        SettingsResolver settings, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var role = await auth.GetRoleAsync(workspaceId, userId, ct);
        var policy = (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Sharing.LinkPolicy;

        var links = await db.Set<ShareLinkEntity>().AsNoTracking()
            .Where(l => l.ProjectId == projectId && l.WorkspaceId == workspaceId)
            .OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
            .ToListAsync(ct);
        var views = await ViewNamesAsync(db, projectId, ct);
        var now = time.GetUtcNow();
        return Results.Ok(links.Select(l => ToDto(l, views, policy, now, userId, role)).ToList());
    }

    public static async Task<IResult> Create(
        Guid projectId, CreateShareLinkRequest request, ClaimsPrincipal user, HttpContext http,
        AppDbContext db, WorkspaceAuthorizationService auth, SettingsResolver settings, SharePageReader reader,
        IPasswordHasher<ShareLinkEntity> hasher, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var now = time.GetUtcNow();

        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        var errors = new List<ValidationError?>
        {
            request.Scope is ShareScope.Project or ShareScope.View ? null : new("scope", "Must be project or view."),
            request.Mode is ShareMode.Live or ShareMode.Pinned ? null : new("mode", "Must be live or pinned."),
            TextRule.Line("label", request.Label, 200),
            password is null ? null : SharePassword.Check("password", password),
            request.ExpiresAt is { } expires && expires <= now ? new("expiresAt", "Must be in the future.") : null,
        };
        if (Refusal([.. errors]) is { } invalid)
            return invalid;

        if (request.Scope == ShareScope.View
            && (request.ViewId is not { } viewId || !await db.Views.AsNoTracking().AnyAsync(v => v.Id == viewId && v.ProjectId == projectId, ct)))
            return Invalid("viewId", "A view link needs the ID of a view of this project");
        if (request.Scope == ShareScope.Project && request.ViewId is not null)
            return Invalid("viewId", "A project link has no view");

        Vellum.Modules.Modelling.RevisionEntity? revision = null;
        if (request.Mode == ShareMode.Pinned)
        {
            revision = request.RevisionId is { } revisionId
                ? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct)
                : null;
            if (revision is null)
                return Invalid("revisionId", "A pinned link needs the ID of a named revision of this project");
        }
        else if (request.RevisionId is not null)
        {
            return Invalid("revisionId", "A live link follows main and has no revision");
        }

        var policy = (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Sharing.LinkPolicy;
        if (ShareLinkPolicy.Refusal(policy, password is not null) is { } refused)
            return Invalid("password", refused);

        if (await db.Set<ShareLinkEntity>().CountAsync(l => l.ProjectId == projectId && l.RevokedAt == null, ct) >= MaxLinksPerProject)
            return Conflict($"A project has at most {MaxLinksPerProject} links that are not revoked. Revoke one first.");

        var token = ShareToken.New();
        var link = new ShareLinkEntity
        {
            Id = Guid.CreateVersion7(now),
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Scope = request.Scope,
            ViewId = request.ViewId,
            Mode = request.Mode,
            RevisionId = revision?.Id,
            RevisionName = revision?.Name,
            RevisionCreatedAt = revision?.CreatedAt,
            Label = request.Label?.Trim() ?? "",
            ShowCosts = request.ShowCosts,
            ShowPromises = request.ShowPromises,
            TokenHash = ShareToken.Hash(token),
            ExpiresAt = request.ExpiresAt,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (password is not null)
            link.PasswordHash = hasher.HashPassword(link, password);

        // A pinned page is made now, from the named revision, and never again.
        ShareSnapshotEntity? snapshot = null;
        if (revision is not null)
        {
            var (model, refusal) = await PinnedModel.ResolveAsync(http.RequestServices, projectId, revision.Id, null, ct);
            if (model is null)
                return refusal!;
            try
            {
                var page = await reader.ReadAsync(new SharePageRequest(
                    projectId, model, new SharedRevision(revision.Name, revision.CreatedAt), now,
                    WithCosts: true, WithPromises: true, WithProjectParts: true), ct);
                snapshot = new ShareSnapshotEntity { LinkId = link.Id, Content = SharePageReader.Serialize(page, json.Value.SerializerOptions) };
            }
            catch (BundleTooLargeException)
            {
                return Invalid("revisionId", "The project is too large to pin in a link. Share a live link instead.");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(link);
        if (snapshot is not null)
            db.Add(snapshot);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, ShareAudit.Created, ShareAudit.Describe(link), ct);
        await transaction.CommitAsync(ct);

        var role = await auth.GetRoleAsync(workspaceId, userId, ct);
        var dto = ToDto(link, await ViewNamesAsync(db, projectId, ct), policy, now, userId, role);
        return Results.Created($"/api/projects/{projectId}/share-links/{link.Id}",
            new ShareLinkCreatedDto(dto, token, $"/s/{token}", link.Scope == ShareScope.View ? $"{EmbedFraming.Prefix}/{token}" : null));
    }

    public static async Task<IResult> Change(
        Guid projectId, Guid linkId, ChangeShareLinkRequest request, ClaimsPrincipal user,
        AppDbContext db, WorkspaceAuthorizationService auth, SettingsResolver settings,
        IPasswordHasher<ShareLinkEntity> hasher, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        var (link, workspaceId, role, refusal) = await ManagedAsync(projectId, linkId, userId, db, auth, ct);
        if (link is null)
            return refusal!;
        var now = time.GetUtcNow();
        if (link.RevokedAt is not null)
            return Conflict("The link is revoked. Make a new one.");
        // An expired link stays expired: a new expiry would open an address that people were told is closed.
        if (link.ExpiresAt <= now)
            return Conflict("The link has expired. Make a new one.");

        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        if (Refusal(
                TextRule.Line("label", request.Label, 200),
                password is null ? null : SharePassword.Check("password", password),
                password is not null && request.RemovePassword ? new ValidationError("removePassword", "Set a password or remove it, not both.") : null,
                request.ExpiresAt is { } expires && expires <= now ? new ValidationError("expiresAt", "Must be in the future.") : null,
                request.ExpiresAt is not null && request.ClearExpiry ? new ValidationError("clearExpiry", "Set an expiry or clear it, not both.") : null)
            is { } invalid)
            return invalid;

        var policy = (await settings.GetWorkspaceAsync(workspaceId, ct)).Effective.Sharing.LinkPolicy;
        var willHavePassword = password is not null || (link.PasswordHash is not null && !request.RemovePassword);
        if ((password is not null || request.RemovePassword) && ShareLinkPolicy.Refusal(policy, willHavePassword) is { } refused)
            return Invalid(request.RemovePassword ? "removePassword" : "password", refused);

        var changed = new List<string>();
        if (request.Label is { } label && label.Trim() != link.Label)
        {
            link.Label = label.Trim();
            changed.Add("label");
        }
        if (request.ShowCosts is { } costs && costs != link.ShowCosts)
        {
            link.ShowCosts = costs;
            changed.Add("costs");
        }
        if (request.ShowPromises is { } promises && promises != link.ShowPromises)
        {
            link.ShowPromises = promises;
            changed.Add("promises");
        }
        if (password is not null)
        {
            link.PasswordHash = hasher.HashPassword(link, password);
            link.PasswordVersion++;
            changed.Add("password");
        }
        else if (request.RemovePassword && link.PasswordHash is not null)
        {
            link.PasswordHash = null;
            link.PasswordVersion++;
            changed.Add("password-removed");
        }
        if (request.ExpiresAt is { } newExpiry && newExpiry != link.ExpiresAt)
        {
            link.ExpiresAt = newExpiry;
            changed.Add("expiry");
        }
        else if (request.ClearExpiry && link.ExpiresAt is not null)
        {
            link.ExpiresAt = null;
            changed.Add("expiry-removed");
        }

        if (changed.Count > 0)
        {
            link.UpdatedAt = now;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await AuditLog.AppendAsync(db, workspaceId, userId, ShareAudit.Changed,
                $"{ShareAudit.Describe(link)} fields:{string.Join(",", changed)}", ct);
            await transaction.CommitAsync(ct);
        }

        return Results.Ok(ToDto(link, await ViewNamesAsync(db, projectId, ct), policy, now, userId, role));
    }

    public static async Task<IResult> Revoke(
        Guid projectId, Guid linkId, ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth,
        TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        var (link, workspaceId, _, refusal) = await ManagedAsync(projectId, linkId, userId, db, auth, ct);
        if (link is null)
            return refusal!;
        if (link.RevokedAt is not null)
            return Results.NoContent();

        link.RevokedAt = time.GetUtcNow();
        link.RevokedBy = userId;
        link.UpdatedAt = link.RevokedAt.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        // A pinned page is of no use once nobody can open it.
        await db.Set<ShareSnapshotEntity>().Where(s => s.LinkId == link.Id).ExecuteDeleteAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, ShareAudit.Revoked, ShareAudit.Describe(link), ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// The link, tracked, when the caller may manage it: an Editor who made it, or an Owner.
    /// A link of another project, or of a project the caller cannot reach, is not found.
    /// </summary>
    private static async Task<(ShareLinkEntity? Link, Guid WorkspaceId, WorkspaceRole? Role, IResult? Refusal)> ManagedAsync(
        Guid projectId, Guid linkId, string userId, AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);
        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var link = await db.Set<ShareLinkEntity>()
            .FirstOrDefaultAsync(l => l.Id == linkId && l.ProjectId == projectId && l.WorkspaceId == workspaceId, ct);
        if (link is null)
            return (null, workspaceId, null, NotFound("Share link"));

        var role = await auth.GetRoleAsync(workspaceId, userId, ct);
        return CanManage(link, userId, role)
            ? (link, workspaceId, role, null)
            : (null, workspaceId, role, Results.Json(
                new ErrorResponse("forbidden", "Only the person who made the link or an Owner can change it"),
                statusCode: StatusCodes.Status403Forbidden));
    }

    private static bool CanManage(ShareLinkEntity link, string userId, WorkspaceRole? role) =>
        role == WorkspaceRole.Owner || (role >= WorkspaceRole.Editor && link.CreatedBy == userId);

    private static Task<Dictionary<Guid, string>> ViewNamesAsync(AppDbContext db, Guid projectId, CancellationToken ct) =>
        db.Views.AsNoTracking().Where(v => v.ProjectId == projectId).ToDictionaryAsync(v => v.Id, v => v.Name, ct);

    private static ShareLinkDto ToDto(
        ShareLinkEntity l, IReadOnlyDictionary<Guid, string> views, string policy, DateTimeOffset now, string userId, WorkspaceRole? role)
    {
        var status = l.RevokedAt is not null ? "revoked"
            : l.ExpiresAt <= now ? "expired"
            : !ShareLinkPolicy.Allows(policy, l.PasswordHash is not null) ? "blocked"
            : "active";
        return new ShareLinkDto(
            l.Id, l.Label, l.Scope, l.ViewId, l.ViewId is { } v ? views.GetValueOrDefault(v) : null, l.Mode,
            l.RevisionId, l.RevisionName, l.RevisionCreatedAt, l.ShowCosts, l.ShowPromises, l.PasswordHash is not null, l.ExpiresAt,
            l.CreatedBy, l.CreatedAt, l.UpdatedAt, l.RevokedAt, l.LastAccessedAt, status, CanManage(l, userId, role));
    }
}
