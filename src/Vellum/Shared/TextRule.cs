namespace Vellum.Shared;

/// <summary>
/// The one rule for text a caller supplies: users, imports and AI output alike. Postgres
/// cannot store U+0000 in <c>text</c> or <c>jsonb</c>, an index row has a size limit, and a
/// control character in a name corrupts every list that shows it. So a value is bounded in
/// length and holds no control character; text of several lines may hold tab and line breaks.
/// A null value passes: whether a value is required is the caller's rule.
/// </summary>
public static class TextRule
{
    /// <summary>Names, labels and other one-line text.</summary>
    public const int MaxName = 200;

    /// <summary>Identifiers a caller chooses, such as step IDs, assertion IDs and tags.</summary>
    public const int MaxIdentifier = 100;

    /// <summary>Descriptions, notes and comment bodies.</summary>
    public const int MaxDescription = 4_000;

    /// <summary>Source pointers, such as <c>github.com/acme/orders@abc123:src/Api/Orders.cs:41-57</c>.</summary>
    public const int MaxPointer = 2_000;

    public const int MaxTags = 50;

    /// <summary>One line: no control character at all.</summary>
    public static ValidationError? Line(string field, string? value, int maxLength = MaxName) =>
        Check(field, value, maxLength, multiline: false);

    /// <summary>Several lines: tab, line feed and carriage return are the only control characters.</summary>
    public static ValidationError? Lines(string field, string? value, int maxLength = MaxDescription) =>
        Check(field, value, maxLength, multiline: true);

    public static ValidationError? Tags(string field, IReadOnlyCollection<string>? tags)
    {
        if (tags is null)
            return null;
        if (tags.Count > MaxTags)
            return new ValidationError(field, $"At most {MaxTags} tags are allowed; this value has {tags.Count}");

        foreach (var tag in tags)
        {
            if (tag is null)
                return new ValidationError(field, "A tag must not be null");
            if (Line(field, tag, MaxIdentifier) is { } error)
                return error;
        }

        return null;
    }

    private static ValidationError? Check(string field, string? value, int maxLength, bool multiline)
    {
        if (value is null)
            return null;
        if (value.Length > maxLength)
            return new ValidationError(field, $"At most {maxLength} characters are allowed; this value has {value.Length}");

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) && !(multiline && c is '\t' or '\n' or '\r'))
            {
                return new ValidationError(field, multiline
                    ? $"Control characters other than tab and line breaks are not allowed (U+{(int)c:X4} at position {i})"
                    : $"Control characters are not allowed (U+{(int)c:X4} at position {i})");
            }

            // Half of a surrogate pair is not text; the database rejects it.
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    i++;
                else
                    return new ValidationError(field, $"The text is not valid Unicode (unpaired surrogate at position {i})");
            }
        }

        return null;
    }
}
