using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Audit;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Identity.AgentTokens;

/// <param name="Scopes">One or more of <c>read</c>, <c>propose</c>, <c>export</c>, <c>scan-pull-request</c>.</param>
/// <param name="ProjectId">Restricts the token to one project of the workspace. Null for every project.</param>
/// <param name="ExpiresAt">Null for 90 days from now. At most 365 days from now.</param>
public sealed record CreateAgentTokenRequest(string? Name, IReadOnlyList<string>? Scopes, Guid? ProjectId, DateTimeOffset? ExpiresAt);

/// <param name="Token">The token. This response is the only place that ever shows it.</param>
public sealed record AgentTokenCreated(
    Guid Id, string Name, string Token, IReadOnlyList<string> Scopes, Guid? ProjectId, DateTimeOffset ExpiresAt);

public sealed record AgentTokenDto(
    Guid Id, string Name, IReadOnlyList<string> Scopes, Guid? ProjectId, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Agent tokens of a workspace: create, list and revoke. Each needs the Owner role and a
/// person signed in to Vellum; a token never makes, lists or revokes a token.
/// </summary>
public static class AgentTokenEndpoints
{
    public static WebApplication MapAgentTokenEndpoints(this WebApplication app)
    {
        var tokens = app.MapGroup("/api/workspaces/{workspaceId}/agent-tokens").WithTags("Agent tokens").RequireHuman();

        tokens.MapPost("/", Create)
            .Produces<AgentTokenCreated>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        tokens.MapGet("/", List)
            .Produces<List<AgentTokenDto>>()
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);
        tokens.MapDelete("/{tokenId}", Revoke)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Create(
        Guid workspaceId, CreateAgentTokenRequest request, ClaimsPrincipal user,
        AppDbContext db, WorkspaceAuthorizationService auth,
        Bootstrap bootstrap, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return Invalid("name", "A name is required");
        if (TextRule.Line("name", name, AgentTokenRules.MaxNameLength) is { } nameError)
            return Invalid(nameError.Field, nameError.Message);

        var scopes = new SortedSet<AgentScope>();
        foreach (var requested in request.Scopes ?? [])
        {
            if (!AgentScopes.TryParse(requested, out var scope))
                return Invalid("scopes", "A scope is read, propose, export or scan-pull-request");
            scopes.Add(scope);
        }
        if (scopes.Count == 0)
            return Invalid("scopes", "At least one scope is required: read, propose, export or scan-pull-request");

        var now = time.GetUtcNow();
        var expiresAt = (request.ExpiresAt ?? now + AgentTokenRules.DefaultLifetime).ToUniversalTime();
        if (expiresAt <= now)
            return Invalid("expiresAt", "The expiry is in the past");
        if (expiresAt > now + AgentTokenRules.MaxLifetime)
            return Invalid("expiresAt", $"A token lives at most {AgentTokenRules.MaxLifetime.Days} days");

        if (request.ProjectId is { } projectId
            && !await db.Projects.AnyAsync(p => p.Id == projectId && p.WorkspaceId == workspaceId, ct))
            return Invalid("projectId", "Must be the ID of a project of this workspace");

        var id = Guid.NewGuid();
        var token = AgentTokenSecret.New(id);
        var keys = bootstrap.Keys;
        var row = new AgentTokenEntity
        {
            Id = id,
            WorkspaceId = workspaceId,
            ProjectId = request.ProjectId,
            Name = name,
            Scopes = scopes.Select(AgentScopes.Name).ToArray(),
            TokenHash = AgentTokenSecret.Digest(keys, keys.Current.Id, token)!,
            MasterKeyId = keys.Current.Id,
            CreatedBy = userId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        // The count, the row and the audit entry commit together. The lock makes the creates
        // of one workspace take turns, so two of them cannot both pass the count.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = workspaceId.ToString("N");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"vellum.agent-tokens:" + lockKey}, 0))", ct);

        var live = await db.AgentTokens.CountAsync(t => t.WorkspaceId == workspaceId && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (live >= AgentTokenRules.MaxLivePerWorkspace)
            return Results.Conflict(new ErrorResponse("conflict",
                $"A workspace holds at most {AgentTokenRules.MaxLivePerWorkspace} agent tokens that are not revoked and not expired",
                "Revoke a token that is no longer used."));

        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM identity.agent_tokens WHERE id IN (
                SELECT id FROM identity.agent_tokens
                WHERE workspace_id = {workspaceId} AND (revoked_at IS NOT NULL OR expires_at <= {now})
                ORDER BY COALESCE(revoked_at, expires_at) DESC, id
                OFFSET {AgentTokenRules.MaxDeadKeptPerWorkspace})
            """, ct);

        db.AgentTokens.Add(row);
        await db.SaveChangesAsync(ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AgentTokenAudit.Created,
            $"agent-token:{id} scopes:{string.Join(",", row.Scopes)} project:{row.ProjectId?.ToString() ?? "all"} expires:{expiresAt:yyyy-MM-ddTHH:mm:ssZ}",
            ct);
        await transaction.CommitAsync(ct);

        return Results.Created($"/api/db/{workspaceId}/agent-tokens/{id}",
            new AgentTokenCreated(id, row.Name, token, row.Scopes, row.ProjectId, row.ExpiresAt));
    }

    /// <summary>
    /// Every token row of the workspace, newest first. A workspace never holds more rows than
    /// the two bounds in <see cref="AgentTokenRules"/> together: a create leaves at most
    /// <see cref="AgentTokenRules.MaxLivePerWorkspace"/> live rows and at most
    /// <see cref="AgentTokenRules.MaxDeadKeptPerWorkspace"/> revoked or expired ones, and
    /// only a create adds a row. No field holds the token or its digest.
    /// </summary>
    private static async Task<IResult> List(
        Guid workspaceId, ClaimsPrincipal user, AppDbContext db, WorkspaceAuthorizationService auth, CancellationToken ct)
    {
        await auth.RequireRoleAsync(workspaceId, user.UserId(), WorkspaceRole.Owner, ct);

        var rows = await db.AgentTokens.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Take(AgentTokenRules.MaxLivePerWorkspace + AgentTokenRules.MaxDeadKeptPerWorkspace)
            .ToListAsync(ct);

        return Results.Ok(rows
            .Select(t => new AgentTokenDto(t.Id, t.Name, t.Scopes, t.ProjectId, t.CreatedBy, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt))
            .ToList());
    }

    /// <summary>
    /// Revokes a token. From the commit on it never authenticates again. A token of another
    /// workspace is not found. Revoking twice changes nothing and answers 204 both times.
    /// </summary>
    private static async Task<IResult> Revoke(
        Guid workspaceId, Guid tokenId, ClaimsPrincipal user,
        AppDbContext db, WorkspaceAuthorizationService auth, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireRoleAsync(workspaceId, userId, WorkspaceRole.Owner, ct);

        if (!await db.AgentTokens.AnyAsync(t => t.Id == tokenId && t.WorkspaceId == workspaceId, ct))
            return Results.NotFound(new ErrorResponse("not_found", "Agent token not found"));

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revoked = await db.Database.ExecuteSqlAsync(
            $"UPDATE identity.agent_tokens SET revoked_at = {now}, revoked_by = {userId} WHERE id = {tokenId} AND workspace_id = {workspaceId} AND revoked_at IS NULL",
            ct);
        if (revoked == 1)
            await AuditLog.AppendAsync(db, workspaceId, userId, AgentTokenAudit.Revoked, $"agent-token:{tokenId}", ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

}
