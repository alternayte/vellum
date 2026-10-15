using Vellum.Shared;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// The model's own text rules (<see cref="TextRule"/>), applied to the text of a model file
/// before it becomes a fact. A value the model would refuse would make the whole proposal
/// impossible to accept, so it is left out here with a note
/// <c>model-file:value-dropped:&lt;field&gt;</c>, and the rest of the fact stays.
/// </summary>
internal static class ModelFileText
{
    /// <summary>The value when it is one line the model takes, else null with a note.</summary>
    public static string? Line(ExtractionContext context, SourceText file, int line, string field, string? value, int max = TextRule.MaxName)
    {
        if (value is null || TextRule.Line(field, value, max) is null)
            return value;
        Dropped(context, file, line, field);
        return null;
    }

    /// <summary>The value when it is text of several lines the model takes, else null with a note.</summary>
    public static string? Lines(ExtractionContext context, SourceText file, int line, string field, string? value)
    {
        if (value is null || TextRule.Lines(field, value) is null)
            return value;
        Dropped(context, file, line, field);
        return null;
    }

    /// <summary>The tags the model takes, comma-joined, at most <see cref="TextRule.MaxTags"/>. Each other tag gives a note.</summary>
    public static string Tags(ExtractionContext context, SourceText file, int line, IEnumerable<string> tags)
    {
        var kept = new List<string>();
        foreach (var tag in tags.Distinct(StringComparer.Ordinal))
        {
            if (kept.Count >= TextRule.MaxTags || tag.Contains(',') || TextRule.Line("tag", tag, TextRule.MaxIdentifier) is not null)
                Dropped(context, file, line, "tag");
            else
                kept.Add(tag);
        }
        return string.Join(',', kept);
    }

    private static void Dropped(ExtractionContext context, SourceText file, int line, string field) =>
        context.Note(NoteKind.Redacted, "model-file:value-dropped:" + field, file.Path, line);
}
