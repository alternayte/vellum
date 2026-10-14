using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vellum.Shared;

/// <summary>
/// The text of an enum member in the HTTP API, the exports and the audit log: the member name
/// in lower-case kebab case (<c>EvidenceOnly</c> is <c>evidence-only</c>), or the name that
/// <see cref="JsonStringEnumMemberNameAttribute"/> gives it. The database stores the same
/// members as labels of a Postgres enum type, in snake_case (<see cref="AppDatabase.EnumLabels"/>);
/// this text is what a reader of the API sees, and it does not change with the column type.
/// </summary>
public static class EnumText
{
    public static string Of<T>(T value) where T : struct, Enum => Names<T>.Text[value];

    /// <summary>The member whose text is exactly <paramref name="text"/>. A number or any other spelling is no member.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null && Names<T>.Values.TryGetValue(text, out value);
    }

    /// <summary>
    /// The member that a filter of a query names; a filter that names no member matches nothing.
    /// Before these columns were enums, the filter text reached Postgres, which refuses U+0000
    /// with 400 <c>invalid_text</c>. A filter keeps that answer.
    /// </summary>
    public static bool TryParseFilter<T>(string text, out T value) where T : struct, Enum
    {
        if (text.Contains('\0'))
            throw new UnstorableTextException();
        return TryParse(text, out value);
    }

    /// <summary>The member whose text is <paramref name="text"/>; a text that names no member is a bug of the caller.</summary>
    public static T Parse<T>(string text) where T : struct, Enum =>
        TryParse<T>(text, out var value) ? value : throw new ArgumentException($"'{text}' is no {typeof(T).Name}", nameof(text));

    private static class Names<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Text = typeof(T)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(
                field => (T)field.GetValue(null)!,
                field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? JsonNamingPolicy.KebabCaseLower.ConvertName(field.Name));

        public static readonly Dictionary<string, T> Values = Text.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }
}
