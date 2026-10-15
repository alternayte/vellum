using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Import.Sources;
using Vellum.Modules.Settings;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Shared.Outbound;
using static Vellum.Shared.ResultExtensions;

namespace Vellum.Modules.Import.Runtime;

/// <summary>What a runtime Source is connected to. The key is never in it: only whether one is stored.</summary>
/// <param name="AccountId">The New Relic account, for a <c>newrelic</c> Source.</param>
/// <param name="Region"><c>us</c> or <c>eu</c>, for a <c>newrelic</c> Source.</param>
/// <param name="Endpoint">The fixed NerdGraph endpoint the Source reads, for a <c>newrelic</c> Source.</param>
/// <param name="KeySet">True when an API key is stored for the Source.</param>
public sealed record RuntimeConnectionDto(long? AccountId, string? Region, string? Endpoint, bool KeySet);

/// <param name="AccountId">The New Relic account ID.</param>
/// <param name="Region"><c>us</c> or <c>eu</c>.</param>
/// <param name="ApiKey">A read-only New Relic User API key. It is sent to New Relic once and not stored.</param>
public sealed record TestNewRelicRequest(long? AccountId, string? Region, string? ApiKey);

/// <param name="ApiKey">A read-only New Relic User API key. It is stored envelope-encrypted and never shown again.</param>
public sealed record SetNewRelicKeyRequest(string? ApiKey);

/// <summary>The result of one real NerdGraph query. A failure is a 200 with <c>ok: false</c>; the error says what to change.</summary>
/// <param name="AccountName">New Relic's name of the account, as text, cut to 300 characters.</param>
public sealed record NewRelicTestResponse(bool Ok, string? AccountName, ConnectionTestError? Error);

/// <param name="ScanId">The scan that turns the derived edges into proposals.</param>
/// <param name="SnapshotId">The SHA-256 of the sorted derived services and edges.</param>
/// <param name="Services">Services the file shows.</param>
/// <param name="Edges">Call and messaging edges the file shows.</param>
/// <param name="Spans">Spans read. None of them is kept.</param>
public sealed record TraceUploadResponse(Guid ScanId, string SnapshotId, int Services, int Edges, string Spans, string Format);

/// <summary>
/// The runtime Sources: a <c>traces</c> Source, which reads trace export files that a person
/// uploads, and a <c>newrelic</c> Source, which reads one New Relic account through NerdGraph.
/// </summary>
public static partial class RuntimeSources
{
    /// <summary>The longest file name an upload is cited under.</summary>
    public const int MaxFileName = 200;

    internal static async Task<IResult> CreateAsync(
        Guid projectId, Guid workspaceId, SourceKind kind, string? name, CreateSourceRequest request, string userId,
        WorkspaceAuthorizationService auth, Bootstrap bootstrap, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        SourceEntity source;
        if (kind == SourceKind.Traces)
        {
            if (name is null)
                return Invalid("name", "Give the trace Source a name, such as \"checkout traces\".");
            if (Slug(name) is not { } locator)
                return Invalid("name", "The name needs at least one letter or digit.");
            source = new SourceEntity
            {
                Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, Kind = kind, Name = name,
                Host = "traces", Locator = locator, Connection = new RuntimeConnection(null, null).ToJson(), CreatedBy = userId, CreatedAt = now,
            };
        }
        else
        {
            // A credential is stored with the Source: only an Owner stores credentials.
            await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
            if (request.AccountId is not ({ } account and > 0 and <= int.MaxValue))
                return Invalid("accountId", "Give the New Relic account ID: a whole number.");
            if (!NewRelicRegions.IsRegion(request.Region))
                return Invalid("region", "The region is us or eu.");
            if (KeyRule(request.ApiKey) is { } keyError)
                return Invalid("apiKey", keyError);
            source = new SourceEntity
            {
                Id = Guid.NewGuid(), ProjectId = projectId, WorkspaceId = workspaceId, Kind = kind,
                Name = name ?? string.Create(CultureInfo.InvariantCulture, $"New Relic account {account}"),
                Host = NewRelicRegions.Host(request.Region!), Locator = string.Create(CultureInfo.InvariantCulture, $"account-{account}"),
                Connection = new RuntimeConnection(account, request.Region).ToJson(), CreatedBy = userId, CreatedAt = now,
            };
        }

        if (await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Host == source.Host && s.Locator == source.Locator, ct) is { } existing)
            return Results.Conflict(new ErrorResponse("source_exists", $"The project already has the Source '{existing.Name}' for {existing.CitationName} ({existing.Id})."));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Sources.Add(source);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new ErrorResponse("source_exists", $"The project already has a Source for {source.CitationName}."));
        }
        if (kind == SourceKind.Newrelic)
            await StoreKeyAsync(db, bootstrap, workspaceId, source, request.ApiKey!, userId, ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/projects/{projectId}/sources/{source.Id}", await SourceRequests.ToDtoAsync(db, source, ct));
    }

    /// <summary>
    /// Reads a trace export from the request body as it arrives, derives its services and
    /// edges, stores only those, and queues the scan that turns them into proposals. The file
    /// itself is never written anywhere. A file that is malformed or over a bound is refused
    /// and nothing is stored.
    /// </summary>
    public static async Task<IResult> Upload(
        Guid projectId, Guid sourceId, string? fileName, HttpContext http, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, SettingsResolver settings, AppDbContext db, ScanSignal signal, TimeProvider time, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (source.Kind != SourceKind.Traces)
            return Results.Conflict(new ErrorResponse("source_kind_mismatch", "Only a traces Source takes a trace file."));
        if (CleanFileName(fileName) is not { } name)
            return Invalid("fileName", $"Give the file name: 1 to {MaxFileName} characters, no control character, no e-mail address and nothing that looks like a credential. Rename the file and upload it again.");

        // The server's own cap is lower than a trace export can be. The endpoint's limit applies instead.
        var limits = http.RequestServices.GetService<TraceLimits>() ?? TraceLimits.Default;
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap)
            cap.MaxRequestBodySize = limits.MaxBytes + 1;
        RuntimeGraph graph;
        try
        {
            graph = await TraceExport.ReadAsync(http.Request.Body, name, limits, ct);
        }
        catch (TraceRefusal refusal)
        {
            return Results.Json(new ErrorResponse(refusal.Code, refusal.Message),
                statusCode: refusal.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest);
        }
        catch (BadHttpRequestException)
        {
            return Results.Json(new ErrorResponse("trace_too_large", "The file is larger than one upload takes. Export a shorter window."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // Every name in the document passed RuntimeGraph.CleanName; nothing else of the file is in it.
        var document = graph.Write();
        var snapshotId = graph.SnapshotId();
        var path = RuntimeFetch.TracePath(name);
        var blobSha = GitBlob.Sha(document);
        var now = time.GetUtcNow();
        var workspace = await settings.GetWorkspaceAsync(source.WorkspaceId, ct);

        var queued = await ScanRequests.QueueAsync(projectId, source, new ScanRequests.ScanOptions(null, null, null, null), user, workspace, db, signal, now, ct,
            prepare: async (scan, innerCt) =>
            {
                scan.CommitSha = snapshotId;
                scan.TreeListed = true;
                scan.FilesListed = 1;
                scan.FilesRead = 1;
                scan.BytesRead = document.LongLength;
                db.ScanFiles.Add(new ScanFileEntity
                {
                    ScanId = scan.Id, Path = path, PathHash = KeyHash.Of(path), BlobSha = blobSha, Size = document.LongLength, State = ScanFileState.Read,
                });
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO import.blobs (workspace_id, blob_sha, content, size, withheld_reason, created_at)
                    VALUES ({source.WorkspaceId}, {blobSha}, {document}, {document.LongLength}, {(string?)null}, {now})
                    ON CONFLICT (workspace_id, blob_sha) DO NOTHING
                    """, innerCt);
            });
        if (queued is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted } || queued is not IValueHttpResult { Value: StartScanResponse started })
            return queued;
        return Results.Accepted($"/api/projects/{projectId}/imports/{started.Id}", new TraceUploadResponse(
            started.Id, snapshotId, graph.Services.Count, graph.Edges.Count, graph.Header.GetValueOrDefault("spans") ?? "0", graph.Header.GetValueOrDefault("exportFormat") ?? ""));
    }

    /// <summary>Replaces the API key of a <c>newrelic</c> Source. Owner only: it is a credential.</summary>
    public static async Task<IResult> SetKey(
        Guid projectId, Guid sourceId, SetNewRelicKeyRequest request, ClaimsPrincipal user,
        WorkspaceAuthorizationService auth, Bootstrap bootstrap, AppDbContext db, CancellationToken ct)
    {
        var userId = user.UserId();
        await auth.RequireProjectRoleAsync(projectId, userId, WorkspaceRole.Owner, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (source.Kind != SourceKind.Newrelic)
            return Results.Conflict(new ErrorResponse("source_kind_mismatch", "Only a newrelic Source has an API key."));
        if (KeyRule(request.ApiKey) is { } keyError)
            return Invalid("apiKey", keyError);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await StoreKeyAsync(db, bootstrap, source.WorkspaceId, source, request.ApiKey!, userId, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Asks NerdGraph whether the stored key can read the account of the Source.</summary>
    internal static async Task<IResult> Test(
        Guid projectId, Guid sourceId, ClaimsPrincipal user, WorkspaceAuthorizationService auth, NewRelicReader reader, AppDbContext db, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Editor, ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
        if (source is null)
            return NotFound("Source");
        if (source.Kind != SourceKind.Newrelic || RuntimeConnection.FromJson(source.Connection) is not { AccountId: { } account, Region: { } region })
            return Results.Conflict(new ErrorResponse("source_kind_mismatch", "Only a newrelic Source has a connection to test."));
        var (key, keyFailure) = await reader.KeyAsync(source.WorkspaceId, source.Id, region, ct);
        if (keyFailure is not null)
            return Results.Ok(new NewRelicTestResponse(false, null, NewRelicReader.Describe(keyFailure)));
        var (name, failure) = await reader.TestAsync(region, key!, account, ct);
        return Results.Ok(failure is null ? new NewRelicTestResponse(true, name, null) : new NewRelicTestResponse(false, null, NewRelicReader.Describe(failure)));
    }

    /// <summary>Tests an account, a region and a key before a Source is made of them. Nothing is stored.</summary>
    internal static async Task<IResult> TestNew(
        Guid projectId, TestNewRelicRequest request, ClaimsPrincipal user, WorkspaceAuthorizationService auth, NewRelicReader reader, CancellationToken ct)
    {
        await auth.RequireProjectRoleAsync(projectId, user.UserId(), WorkspaceRole.Owner, ct);
        if (request.AccountId is not ({ } account and > 0 and <= int.MaxValue))
            return Invalid("accountId", "Give the New Relic account ID: a whole number.");
        if (!NewRelicRegions.IsRegion(request.Region))
            return Invalid("region", "The region is us or eu.");
        if (KeyRule(request.ApiKey) is { } keyError)
            return Invalid("apiKey", keyError);
        var (name, failure) = await reader.TestAsync(request.Region!, new OutboundCredential("API-Key", null, request.ApiKey!), account, ct);
        return Results.Ok(failure is null ? new NewRelicTestResponse(true, name, null) : new NewRelicTestResponse(false, null, NewRelicReader.Describe(failure)));
    }

    internal static async Task<RuntimeConnectionDto?> ConnectionAsync(AppDbContext db, SourceEntity source, CancellationToken ct)
    {
        if (!RuntimeFetch.IsRuntime(source.Kind))
            return null;
        var connection = RuntimeConnection.FromJson(source.Connection);
        if (source.Kind == SourceKind.Traces)
            return new RuntimeConnectionDto(null, null, null, false);
        var region = connection.Region ?? NewRelicRegions.Us;
        var secret = SecretNames.NewRelicApiKey(source.Id, region);
        var keySet = await db.Secrets.AsNoTracking().AnyAsync(s => s.WorkspaceId == source.WorkspaceId && s.Name == secret, ct);
        return new RuntimeConnectionDto(connection.AccountId, region, NewRelicRegions.Endpoint(region).ToString(), keySet);
    }

    /// <summary>Seals the key for the fixed endpoint of the Source's region, in the caller's transaction, and audits it. The value is cleared from memory after.</summary>
    private static async Task StoreKeyAsync(AppDbContext db, Bootstrap bootstrap, Guid workspaceId, SourceEntity source, string apiKey, string userId, CancellationToken ct)
    {
        var region = RuntimeConnection.FromJson(source.Connection).Region ?? NewRelicRegions.Us;
        var name = SecretNames.NewRelicApiKey(source.Id, region);
        var row = new SecretEntity { WorkspaceId = workspaceId, Name = name };
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            SecretEnvelope.Seal(bootstrap.Keys, row, OutboundUrlPolicy.OriginOf(NewRelicRegions.Endpoint(region)), bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO settings.secrets
                (workspace_id, name, master_key_id, wrap_nonce, wrapped_key, nonce, ciphertext, fingerprint, bound_origin, updated_at, updated_by)
            VALUES
                ({workspaceId}, {name}, {row.MasterKeyId}, {row.WrapNonce}, {row.WrappedKey},
                 {row.Nonce}, {row.Ciphertext}, {row.Fingerprint}, {row.BoundOrigin}, now(), {userId})
            ON CONFLICT (workspace_id, name) DO UPDATE
            SET master_key_id = excluded.master_key_id, wrap_nonce = excluded.wrap_nonce, wrapped_key = excluded.wrapped_key,
                nonce = excluded.nonce, ciphertext = excluded.ciphertext, fingerprint = excluded.fingerprint,
                bound_origin = excluded.bound_origin, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, ct);
        await AuditLog.AppendAsync(db, workspaceId, userId, AuditLog.SecretSet, name, ct);
    }

    private static string? KeyRule(string? key) =>
        key is not { Length: >= 1 and <= SecretEnvelope.MaxValueLength } ? $"Give the API key: 1 to {SecretEnvelope.MaxValueLength} characters."
        : SecretNames.ValueRuleBrokenBy(SecretNames.NewRelicApiKey(Guid.NewGuid(), NewRelicRegions.Us), key);

    /// <summary>The locator of a trace Source: its name in lower case, with every run of other characters as one hyphen.</summary>
    private static string? Slug(string name)
    {
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 100)
            slug = slug[..100].Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>The last part of the name, without control characters: the file is cited under it.</summary>
    internal static string? CleanFileName(string? fileName)
    {
        if (fileName is null)
            return null;
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        if (name.Length is 0 or > MaxFileName || name is "." or "..")
            return null;
        // The name is cited in every fact and provenance of the upload, so it passes the same
        // checks as a name in the trace: no control character, no e-mail address, no credential.
        return RuntimeGraph.CleanName(name) == name ? name : null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
