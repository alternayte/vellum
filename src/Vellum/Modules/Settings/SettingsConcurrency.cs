using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Settings;

/// <summary>
/// Transaction-level advisory locks that order settings writes. A workspace save holds the
/// instance lock in shared mode, so the instance policy it was checked against cannot change
/// before it commits. A secret save and a settings save of one workspace take turns.
/// </summary>
internal static class SettingsLocks
{
    // Any constant works; every Vellum instance must use the same one.
    private const long InstanceKey = 0x56454C53455454;

    public static Task InstanceSharedAsync(DbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared({InstanceKey})", ct);

    public static Task InstanceExclusiveAsync(DbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({InstanceKey})", ct);

    public static Task WorkspaceAsync(DbContext db, Guid workspaceId, CancellationToken ct)
    {
        var key = $"vellum.workspace-settings:{workspaceId:N}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}

/// <summary>
/// Settings carry a version that goes up by one on every save. A reader gets it in the body
/// and in the <c>ETag</c> header. A writer sends it back in <c>If-Match</c>, so a save that
/// started from an old copy is refused and does not undo another person's change.
/// </summary>
internal static class SettingsVersion
{
    public static string ETag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>The version in <c>If-Match</c>, or the refusal: 428 when the header is missing, 400 when it is not a version.</summary>
    public static long? ReadIfMatch(HttpRequest request, out IResult? refusal)
    {
        refusal = null;
        var header = request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            refusal = Results.Json(
                new ErrorResponse("precondition_required", "The If-Match header is missing",
                    "Read the settings, then send the version you read as If-Match: \"<version>\"."),
                statusCode: StatusCodes.Status428PreconditionRequired);
            return null;
        }

        if (header.Length >= 3 && header[0] == '"' && header[^1] == '"'
            && long.TryParse(header.AsSpan(1, header.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return version;

        refusal = Results.BadRequest(new ErrorResponse("validation_error", "Validation failed",
            Errors: [new FieldError("If-Match", "Must be the version in quotes, for example \"3\".")]));
        return null;
    }

    public static IResult Conflict(long current) => Results.Json(
        new ErrorResponse("conflict", "The settings changed after you read them",
            $"The current version is {current.ToString(CultureInfo.InvariantCulture)}. Read the settings again, apply your change and save."),
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>The body with its version in the <c>ETag</c> header.</summary>
    public static IResult Ok<T>(HttpResponse response, long version, T body)
    {
        response.Headers.ETag = ETag(version);
        return Results.Ok(body);
    }
}
