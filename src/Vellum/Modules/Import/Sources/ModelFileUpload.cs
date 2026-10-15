using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Sources;

/// <param name="Path">
/// Where the file stands, relative, with <c>/</c>: <c>workspace.dsl</c>, <c>catalog-info.yaml</c>,
/// <c>docs/adr/0001-use-postgres.md</c>. The extractors decide by the path: an ADR must stand in
/// <c>docs/adr/</c>, <c>doc/adr/</c> or <c>docs/decisions/</c>.
/// </param>
/// <param name="Content">The text of the file, at most <see cref="ModelFileUpload.MaxBytes"/> bytes as UTF-8.</param>
/// <param name="Name">What people call the Source. Without it the Source is named after the path.</param>
public sealed record UploadModelFileRequest(string? Path, string? Content, string? Name = null);

/// <summary>The Source the file is a Snapshot of, and the scan that reads it.</summary>
public sealed record UploadModelFileResponse(Guid SourceId, Guid ScanId);

/// <summary>
/// One file as its own small Source of kind <c>model-file</c>: a Structurizr workspace, a
/// Backstage catalog, an ADR, or a Markdown or text document for the prose import. Each upload
/// of the same path is a new Snapshot of the same Source. The file goes through the same rules
/// as a file of a repository: the path filter (no secret file, no binary), the workspace's
/// <c>import.excludeGlobs</c>, the size bounds and the content clearance; content that declares
/// a secret is refused and not stored. The upload pins the Snapshot (its ID is the revision) and
/// lists the one file; the scan then extracts and proposes like any other.
/// </summary>
public static class ModelFileUpload
{
    /// <summary>The largest file an upload takes, before the workspace's own <c>import.maxFileBytes</c>.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The host part of the citation of every <c>model-file</c> Source.</summary>
    public const string Host = "model-file";

    public static async Task<IResult> Upload(
        Guid projectId, UploadModelFileRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db,
        ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Editor, ct);

        var path = request.Path?.Trim() ?? "";
        if (path.Length is 0 or > 255)
            return Invalid("path", "Give the path of the file, such as workspace.dsl or docs/adr/0001-use-postgres.md, of at most 255 characters.");
        if (PathFilter.SkipReason(path, out _) is { } skip)
            return Refused("path_refused", $"The path is not read ({skip}). {ScanNotes.Message(skip)}");
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!PathFilter.IsRelevantPath(path) && !PathFilter.IsProse(name))
            return Refused("not_a_model_file", "Upload a Structurizr DSL (.dsl) or workspace JSON (.json), a Backstage catalog (.yaml), an ADR or another Markdown or text document (.md, .txt).");
        var sourceName = string.IsNullOrWhiteSpace(request.Name) ? path : request.Name.Trim();
        if (TextRule.Line("name", sourceName, SourceEntity.MaxName) is { } nameError)
            return Invalid(nameError);

        var workspaceId = await auth.GetProjectWorkspaceIdAsync(projectId, ct);
        var workspace = await settings.GetWorkspaceAsync(workspaceId, ct);
        var import = workspace.Effective.Import;
        var limit = Math.Min(MaxBytes, import.MaxFileBytes);
        var content = Encoding.UTF8.GetBytes(request.Content ?? "");
        if (content.Length == 0)
            return Invalid("content", "The file is empty.");
        if (content.LongLength > limit)
            return Refused("file_too_large", $"The file holds {content.Length} bytes. An upload holds at most {limit} bytes.");
        if (Array.IndexOf(content, (byte)0) >= 0)
            return Refused("binary_content", "The file is not text.");
        if (ContentClearance.Withhold(content) is { } withheld)
            return Refused("content_withheld", $"The file was not stored ({withheld}). {ScanNotes.Message(withheld)}");

        var blobSha = GitBlob.Sha(content);
        var entry = new GitHubTreeEntry(path, "blob", blobSha, content.LongLength, "100644");
        var limits = new ScanLimits(import.MaxFiles, import.MaxFileBytes, import.MaxTotalBytes, [.. import.ExcludeGlobs]);
        // A document is not a file the scan reads (not-relevant); the prose import reads it. Every other reason refuses it.
        if (ScanScope.Select([entry], limits, [], []).Files.FirstOrDefault()?.Reason is { } outOfScope && outOfScope != CoverageGap.NotRelevant)
            return Refused("path_refused", $"The workspace's import settings leave this path out ({outOfScope}).");

        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO import.sources (id, project_id, workspace_id, kind, name, host, locator, connection, created_by, created_at)
            VALUES ({Guid.NewGuid()}, {projectId}, {workspaceId}, 'model_file', {sourceName}, {Host}, {path}, {"{}"}::jsonb, {userId}, {now})
            ON CONFLICT (project_id, host, locator) DO NOTHING
            """, ct);
        var source = await db.Sources.AsNoTracking().FirstAsync(s => s.ProjectId == projectId && s.Host == Host && s.Locator == path, ct);
        if (source.Kind != SourceKind.ModelFile)
            return Refused("source_exists", $"The project has the Source '{source.Name}' at this place already.");

        var manifest = SnapshotManifest.Id([new ManifestEntry(path, content.LongLength, blobSha)]);
        var queued = await ScanRequests.QueueAsync(projectId, source, new ScanRequests.ScanOptions(null, null, null, null),
            user, workspace, db, signal, now, ct, async scan =>
            {
                // The Snapshot is pinned by the upload: its ID is its revision. The blob is the
                // cleared content, keyed by its git blob SHA like every other.
                scan.CommitSha = manifest;
                scan.RepositoryName = source.Name;
                scan.TreeListed = true;
                scan.FilesListed = 1;
                scan.FilesRead = 1;
                scan.BytesRead = content.LongLength;
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO import.blobs (workspace_id, blob_sha, content, size, withheld_reason, created_at)
                    VALUES ({workspaceId}, {blobSha}, {content}, {content.LongLength}, {null}, {now})
                    ON CONFLICT (workspace_id, blob_sha) DO NOTHING
                    """, ct);
                db.ScanFiles.Add(new ScanFileEntity
                {
                    ScanId = scan.Id, Path = path, PathHash = KeyHash.Of(path), BlobSha = blobSha,
                    Size = content.LongLength, State = ScanFileState.Read,
                });
            });
        return queued is IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted } && queued is IValueHttpResult { Value: StartScanResponse started }
            ? Results.Accepted($"/api/projects/{projectId}/imports/{started.Id}", new UploadModelFileResponse(source.Id, started.Id))
            : queued;
    }

    private static IResult Refused(string code, string message) => Results.BadRequest(new ErrorResponse(code, message));
}
