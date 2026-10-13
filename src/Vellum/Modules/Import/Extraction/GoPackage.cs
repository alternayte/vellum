using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <param name="JsonName">The name <c>encoding/json</c> reads: the <c>json</c> tag, or the field name.</param>
/// <param name="Type">The Go type as written, without spaces except between two words.</param>
/// <param name="References">The unqualified type names in <paramref name="Type"/>; one of them can name a struct of the package.</param>
internal readonly record struct GoField(string JsonName, string Type, string[] References);

/// <summary>A struct type declared at package level, as <c>encoding/json</c> sees it.</summary>
/// <param name="Unsupported">True when the fields cannot be stated exactly (see <see cref="GoPackage"/>). No fact is made from such a struct.</param>
internal sealed record GoStruct(string Name, string Path, int Line, int EndLine, IReadOnlyList<GoField> Fields, bool Unsupported);

/// <summary>
/// The package-level declarations of one Go package (the files of one directory) that the
/// rules resolve names against: string constants and struct types. It is filled from the
/// tokens of each file in one pass over the top-level tokens; nothing of a function body is read.
/// <list type="bullet">
/// <item>A constant is <c>const name = "literal"</c> or <c>const name string = "literal"</c>, alone
/// or in a <c>const ( … )</c> group, where the literal is the whole value. A name that has two
/// different values in the package, or that the package also declares as a variable, function
/// or type, is not a constant here.</item>
/// <item>A struct is <c>type Name struct { … }</c>, alone or in a <c>type ( … )</c> group. A
/// generic type and an alias are not read. A name that is declared twice is not a struct here.</item>
/// <item>A struct is unsupported when it has an embedded field, a field whose type holds a
/// non-empty <c>struct { }</c> or <c>interface { }</c> body, parentheses (a function type) or
/// more than <see cref="MaxTypeTokens"/> tokens, two fields with one JSON name, or a JSON name
/// with a ':' or ';'. Unexported fields and <c>json:"-"</c> fields are left out, as the decoder
/// leaves them out. Tag options (<c>omitempty</c>, <c>string</c>) are not recorded.</item>
/// </list>
/// </summary>
internal sealed class GoPackage
{
    /// <summary>The largest number of tokens in the type of one field.</summary>
    public const int MaxTypeTokens = 24;

    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notConstant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GoStruct?> _structs = new(StringComparer.Ordinal);

    /// <summary>The value of a package-level string constant, or null.</summary>
    public string? Constant(string name) =>
        !_notConstant.Contains(name) && _constants.TryGetValue(name, out var value) ? value : null;

    /// <summary>The struct type of that name when the package declares it exactly once, or null.</summary>
    public GoStruct? Struct(string name) => _structs.GetValueOrDefault(name);

    /// <summary>
    /// Reads the top-level declarations of one file. True when the file is in package
    /// <c>main</c> and declares <c>func main()</c>.
    /// </summary>
    public bool Declare(SourceText file, TokenList t, WorkMeter meter)
    {
        var isMain = t.Id(0, "package") && t.Id(1, "main");
        var hasMain = false;
        if (!meter.Charge(t.Count))
            return false;
        for (var i = 0; i < t.Count; i++)
        {
            if (t.Parent[i] >= 0 || !t.Id(i))
                continue;
            var keyword = t[i].Text;
            if (keyword is not ("const" or "var" or "type" or "func"))
                continue;
            if (keyword == "func")
            {
                // "func (r T) Name(" is a method and declares no package-level name.
                if (t.Id(i + 1) && (t.P(i + 2, '(') || t.P(i + 2, '[')))
                {
                    _notConstant.Add(t[i + 1].Text);
                    hasMain |= isMain && t.Id(i + 1, "main") && t.P(i + 2, '(');
                }
                continue;
            }
            if (t.P(i + 1, '(') && t.Match[i + 1] > i)
            {
                var open = i + 1;
                var close = t.Match[open];
                for (var k = open + 1; k < close; k++)
                {
                    if (!meter.Charge())
                        return hasMain;
                    if (t.Parent[k] != open)
                        continue;
                    if (k == open + 1 || t.Line(k) > t.EndLine(k - 1) || t.P(k - 1, ';'))
                        Spec(file, t, keyword, k, k, meter);
                }
                i = close;
            }
            else
            {
                Spec(file, t, keyword, i + 1, i, meter);
            }
        }
        return hasMain;
    }

    private void Spec(SourceText file, TokenList t, string keyword, int name, int first, WorkMeter meter)
    {
        if (!t.Id(name))
            return;
        var text = t[name].Text;
        switch (keyword)
        {
            case "const":
                if (StringConstant(t, name) is { } value)
                {
                    if (_constants.TryGetValue(text, out var existing) && existing != value)
                        _notConstant.Add(text);
                    _constants[text] = value;
                }
                else
                {
                    // "a, b = …", a typed number, an expression: declared, with no value known here.
                    Names(t, name, meter);
                }
                break;
            case "var":
                Names(t, name, meter);
                break;
            default:
                _notConstant.Add(text);
                if (t.Id(name + 1, "struct") && t.P(name + 2, '{') && t.Match[name + 2] > name + 2)
                    _structs[text] = _structs.ContainsKey(text) ? null : ReadStruct(file, t, text, first, name + 2, meter);
                else
                    _structs[text] = null;
                break;
        }
    }

    /// <summary>"a, b, c …": every name of the list is declared and is not a constant with a known value.</summary>
    private void Names(TokenList t, int name, WorkMeter meter)
    {
        for (var k = name; t.Id(k) && meter.Charge(); k += 2)
        {
            _notConstant.Add(t[k].Text);
            if (!t.P(k + 1, ','))
                break;
        }
    }

    /// <summary>The value of <c>name = "literal"</c> or <c>name string = "literal"</c> when the literal ends the declaration.</summary>
    public static string? StringConstant(TokenList t, int name)
    {
        var equals = t.P(name + 1, '=') ? name + 1 : t.Id(name + 1) && t.P(name + 2, '=') ? name + 2 : -1;
        if (equals < 0 || t.StaticString(equals + 1) is not { } value)
            return null;
        // "a" + suffix is not a literal value.
        var next = equals + 2;
        return next >= t.Count || t.Line(next) > t.EndLine(equals + 1) || t.P(next, ')') || t.P(next, ';') || t.P(next, '}') ? value : null;
    }

    private static GoStruct ReadStruct(SourceText file, TokenList t, string name, int first, int open, WorkMeter meter)
    {
        var close = t.Match[open];
        var fields = new List<GoField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = false;
        var k = open + 1;
        while (k < close)
        {
            // One field declaration: from the first token of a line to the last token before the next one.
            var start = k;
            var end = k;
            while (k < close)
            {
                if (!meter.Charge())
                    return new GoStruct(name, file.Path, t.Line(first), t.EndLine(close), [], true);
                end = t.Match[k] > k ? t.Match[k] : k;
                k = end + 1;
                if (t.P(end, ';') || (k < close && t.Line(k) > t.EndLine(end)))
                    break;
            }
            if (t.P(end, ';'))
                end--;
            if (end >= start && !unsupported)
                unsupported = !ReadField(t, start, end, fields, seen, meter);
        }
        return new GoStruct(name, file.Path, t.Line(first), t.EndLine(close), unsupported ? [] : fields, unsupported);
    }

    /// <summary>False when the declaration cannot be stated exactly.</summary>
    private static bool ReadField(TokenList t, int start, int end, List<GoField> fields, HashSet<string> seen, WorkMeter meter)
    {
        string? tag = null;
        if (t[end].Kind == TokKind.String)
        {
            tag = t[end].Text;
            end--;
        }
        // "Name Type" or "A, B Type". Anything else ("T", "*T", "pkg.T") is an embedded field.
        if (!t.Id(start) || end <= start || t.P(start + 1, '.'))
            return false;
        var type = start + 1;
        while (t.P(type, ',') && t.Id(type + 1) && type + 2 <= end && meter.Charge())
            type += 2;
        if (t.P(type, ',') || end - type + 1 > MaxTypeTokens)
            return false;

        var text = new StringBuilder();
        var references = new List<string>();
        for (var k = type; k <= end; k++)
        {
            var token = t[k];
            switch (token.Kind)
            {
                case TokKind.String or TokKind.Char:
                    return false;
                case TokKind.Punct when token.Text[0] is '(' or ')' or ';':
                    return false;
                case TokKind.Punct when token.Text[0] == '{':
                    // Only the empty bodies of "interface{}" and "struct{}" have no fields of their own.
                    if (!t.P(k + 1, '}') || k + 1 > end)
                        return false;
                    break;
                case TokKind.Ident or TokKind.Number:
                    if (k > type && t[k - 1].Kind is TokKind.Ident or TokKind.Number)
                        text.Append(' ');
                    if (token.Kind == TokKind.Ident && !t.P(k - 1, '.') && !t.P(k + 1, '.'))
                        references.Add(token.Text);
                    break;
            }
            text.Append(token.Text);
        }

        var (jsonName, skip) = JsonName(tag);
        for (var k = start; k < type; k += 2)
        {
            var field = t[k].Text;
            if (!char.IsUpper(field[0]) || skip)
                continue;
            var name = jsonName ?? field;
            if (name.Contains(':') || name.Contains(';') || !seen.Add(name))
                return false;
            fields.Add(new GoField(name, text.ToString(), [.. references]));
        }
        return true;
    }

    /// <summary>
    /// The name in the <c>json</c> key of a struct tag (<c>key:"value" key:"value"</c>), or null
    /// when the tag has none; and whether the tag is <c>json:"-"</c>, which leaves the field out.
    /// </summary>
    private static (string? Name, bool Skip) JsonName(string? tag)
    {
        if (tag is null)
            return (null, false);
        var i = 0;
        while (i < tag.Length)
        {
            while (i < tag.Length && tag[i] == ' ')
                i++;
            var keyStart = i;
            while (i < tag.Length && tag[i] > ' ' && tag[i] != ':' && tag[i] != '"')
                i++;
            if (i == keyStart || i + 1 >= tag.Length || tag[i] != ':' || tag[i + 1] != '"')
                return (null, false);
            var key = tag.AsSpan(keyStart, i - keyStart);
            i += 2;
            var valueStart = i;
            while (i < tag.Length && tag[i] != '"')
                i += tag[i] == '\\' ? 2 : 1;
            if (i >= tag.Length)
                return (null, false);
            var value = tag[valueStart..i];
            i++;
            if (!key.SequenceEqual("json"))
                continue;
            if (value == "-")
                return (null, true);
            var comma = value.IndexOf(',');
            var name = comma < 0 ? value : value[..comma];
            return (name.Length == 0 ? null : name, false);
        }
        return (null, false);
    }
}
