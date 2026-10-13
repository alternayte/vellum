using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace Vellum.Shared;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

/// <summary>
/// A cursor that is not one a list gave. A list never reads it as "no cursor": that would
/// give the first page again to a caller that asked for a later one.
/// </summary>
public sealed class InvalidCursorException() : Exception(Text)
{
    public const string Field = "cursor";
    public const string Text = "The cursor is not one that this list gave. Send the cursor of the page before, or leave it out for the first page.";

    public static ErrorResponse Error => new("validation_error", "Validation failed", Errors: [new FieldError(Field, Text)]);
}

public static class CursorEncoder
{
    public static string Encode(string sortKey, Guid id)
    {
        var json = JsonSerializer.Serialize(new { s = sortKey, i = id });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// The sort key and the ID of the last item of the page before. Null when no cursor was
    /// sent: the list starts at its first page.
    /// </summary>
    /// <exception cref="InvalidCursorException">The text is not a cursor. The caller gets 400 on the field <c>cursor</c>.</exception>
    public static (string SortKey, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("s").GetString() is { } sortKey)
                return (sortKey, doc.RootElement.GetProperty("i").GetGuid());
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // Not a cursor: the same answer as a cursor with no sort key.
        }

        throw new InvalidCursorException();
    }

    /// <summary><see cref="Decode"/> for a list whose sort key is a whole number that is not negative.</summary>
    /// <exception cref="InvalidCursorException">The text is not a cursor of such a list.</exception>
    public static (long Number, Guid Id)? DecodeNumber(string? cursor) =>
        Decode(cursor) is not { } decoded ? null
        : long.TryParse(decoded.SortKey, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? (number, decoded.Id)
        : throw new InvalidCursorException();
}

/// <summary>Answers a cursor that is not a cursor with 400 <c>validation_error</c> on the field <c>cursor</c>, on every list.</summary>
internal sealed class InvalidCursorExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not InvalidCursorException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(InvalidCursorException.Error, cancellationToken);
        return true;
    }
}
