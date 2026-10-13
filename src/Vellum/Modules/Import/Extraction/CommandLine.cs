using System.Text;
using System.Text.Json;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// The one place that turns a command of a deployment descriptor into text for a fact. A
/// command line is where passwords and tokens are passed, so no argument VALUE is kept:
/// <list type="bullet">
/// <item>The executable: the first word, when the command has one and the word is made of
/// letters, digits and <c>_ . + / -</c> (at most 128 characters). A first word that starts
/// with '-' is a flag: the command is then an argument list without an executable.</item>
/// <item>The NAMES of flags, in the order written: <c>--name</c> (letters, digits and
/// <c>_ . -</c>, at most 64 characters) and <c>-x</c> (one letter or digit). The part from
/// '=' on is cut: <c>--flag=value</c> gives <c>--flag</c>.</item>
/// <item>Nothing else. Every word that does not start with '-' is a value or a positional
/// argument and is left out, and so is a single-dash word of more than one character
/// (<c>-phunter2</c>, <c>-xzf</c>, <c>-config</c>): it can be a flag with its value attached.</item>
/// </list>
/// The four users are the Dockerfile <c>CMD</c>/<c>ENTRYPOINT</c>, the compose
/// <c>command</c>/<c>entrypoint</c>, the Kubernetes <c>command</c>/<c>args</c>, and the
/// classification of a workflow <c>run</c> script (which emits no text at all).
/// <para>
/// A command in shell form is split into words with the quoting rules of a POSIX shell
/// (single quotes, double quotes, backslash). Only the first simple command is read: it ends
/// at an operator outside quotes (<c>; &amp; | &lt; &gt; ( )</c>, a backquote, a line end) or
/// at a comment. Leading <c>NAME=value</c> words are assignments and not the executable.
/// Nothing is expanded or run.
/// </para>
/// </summary>
internal static class CommandLine
{
    private const int MaxExecutableLength = 128;
    private const int MaxFlagLength = 64;

    /// <summary>
    /// The words of the first simple command of a shell line, without leading assignments.
    /// At most <paramref name="maxWords"/> words are read.
    /// </summary>
    public static List<string> SimpleCommand(ReadOnlySpan<char> text, int maxWords = int.MaxValue)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var onlyAssignments = true;

        bool Flush()
        {
            if (!inWord)
                return true;
            var value = word.ToString();
            word.Clear();
            inWord = false;
            if (onlyAssignments && IsAssignment(value))
                return true;
            onlyAssignments = false;
            words.Add(value);
            return words.Count < maxWords;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                inWord = true;
                for (i++; i < text.Length && text[i] != '\''; i++)
                    word.Append(text[i]);
            }
            else if (c == '"')
            {
                inWord = true;
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                        i++;
                    word.Append(text[i]);
                }
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                // A backslash before a line end joins the lines; before anything else it quotes it.
                if (text[++i] != '\n')
                {
                    inWord = true;
                    word.Append(text[i]);
                }
            }
            else if (c is ' ' or '\t')
            {
                if (!Flush())
                    return words;
            }
            else if (c is ';' or '&' or '|' or '<' or '>' or '(' or ')' or '`' or '\n' || (c == '#' && !inWord))
            {
                break;
            }
            else
            {
                inWord = true;
                word.Append(c);
            }
        }
        Flush();
        return words;
    }

    /// <summary>The words of a command in exec form (<c>["a", "b"]</c>), or null when the text is not a JSON array of strings.</summary>
    public static List<string>? ExecForm(string text)
    {
        if (text.Length == 0 || text[0] != '[')
            return null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var words = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return null;
                words.Add(element.GetString() ?? "");
            }
            return words;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The reduced form of a command: the executable (when the command has one) and the flag
    /// names, joined with spaces. Null when nothing of the command may be kept. A first word
    /// that cannot be an executable name gives null and a <c>command-not-reduced</c> note.
    /// </summary>
    /// <param name="hasExecutable">False for an argument list (Kubernetes <c>args</c>, a command beside an entrypoint).</param>
    public static string? Reduce(IReadOnlyList<string> words, bool hasExecutable, ExtractionContext context, string path, int line)
    {
        if (!context.Meter(path).Charge(words.Count))
            return null;
        var parts = new List<string>();
        var first = 0;
        if (hasExecutable && words.Count > 0 && !words[0].StartsWith('-'))
        {
            if (!IsExecutable(words[0]))
            {
                context.Note(NoteKind.Undecided, "command-not-reduced", path, line);
                return null;
            }
            parts.Add(words[0]);
            first = 1;
        }
        for (var i = first; i < words.Count; i++)
            if (FlagName(words[i]) is { } flag)
                parts.Add(flag);
        return parts.Count == 0 ? null : context.List(parts, path, line, separator: " ");
    }

    /// <summary>The reduced form of a command in shell form.</summary>
    public static string? ReduceShell(string text, bool hasExecutable, ExtractionContext context, string path, int line) =>
        context.Meter(path).Charge(text.Length) ? Reduce(SimpleCommand(text), hasExecutable, context, path, line) : null;

    /// <summary>
    /// Every run of letters, digits and <c>_ . + / -</c> in the text, also inside quotes and
    /// after operators. This is for classification only: nothing it returns enters a fact.
    /// </summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && IsNameCharacter(text[i]))
            {
                if (start < 0)
                    start = i;
                continue;
            }
            if (start >= 0)
                yield return text[start..i];
            start = -1;
        }
    }

    /// <summary>"/usr/bin/pg_dump" gives "pg_dump".</summary>
    public static string BaseName(string word) => word[(word.LastIndexOf('/') + 1)..];

    /// <summary>The name of a flag without its value, or null for a word that is not a flag that may be kept.</summary>
    private static string? FlagName(string word)
    {
        if (word.Length < 2 || word[0] != '-')
            return null;
        var dashes = word[1] == '-' ? 2 : 1;
        var equals = word.IndexOf('=');
        var name = equals < 0 ? word : word[..equals];
        var length = name.Length - dashes;
        if (length <= 0 || name.Length > MaxFlagLength || (dashes == 1 && length != 1) || !char.IsAsciiLetterOrDigit(name[dashes]))
            return null;
        for (var i = dashes; i < name.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] is '_' or '-' or '.'))
                return null;
        return name;
    }

    private static bool IsExecutable(string word)
    {
        if (word.Length is 0 or > MaxExecutableLength)
            return false;
        foreach (var c in word)
            if (!IsNameCharacter(c))
                return false;
        return true;
    }

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '/' or '-';

    /// <summary>"NAME=value" with a valid variable name.</summary>
    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && IsVariableName(word.AsSpan(0, equals));
    }

    /// <summary><c>[A-Za-z_][A-Za-z0-9_]*</c></summary>
    public static bool IsVariableName(ReadOnlySpan<char> name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }
}
