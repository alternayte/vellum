using System.Text;
using System.Text.Json;

namespace Vellum.Shared;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);

public static class CursorEncoder
{
    public static string Encode(string sortKey, Guid id)
    {
        var json = JsonSerializer.Serialize(new { s = sortKey, i = id });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static (string SortKey, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var doc = JsonDocument.Parse(json);
            var sortKey = doc.RootElement.GetProperty("s").GetString()!;
            var id = doc.RootElement.GetProperty("i").GetGuid();
            return (sortKey, id);
        }
        catch
        {
            return null;
        }
    }
}
