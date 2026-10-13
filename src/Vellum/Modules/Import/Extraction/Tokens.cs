namespace Vellum.Modules.Import.Extraction;

internal enum TokKind { Ident, Number, String, Char, Punct }

/// <param name="Text">For a string: the decoded value. For punctuation: one character.</param>
/// <param name="IsStatic">False for a string whose value is not known from the text alone (an interpolated string with a hole).</param>
internal readonly record struct Tok(TokKind Kind, string Text, int Line, int EndLine, bool IsStatic);

/// <summary>
/// The tokens of one source file, with bracket pairs matched. Comments are gone and a string
/// literal is one token, so a rule that reads tokens cannot see code inside either.
/// </summary>
internal sealed class TokenList
{
    private static readonly string[] Punctuation = Enumerable.Range(0, 128).Select(c => ((char)c).ToString()).ToArray();

    private readonly List<Tok> _tokens;

    public TokenList(List<Tok> tokens)
    {
        _tokens = tokens;
        Match = new int[tokens.Count];
        Parent = new int[tokens.Count];
        Depth = new int[tokens.Count];
        var open = new Stack<int>();
        // Open brackets of each kind, so that a stray closer costs nothing.
        int round = 0, square = 0, curly = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            Match[i] = -1;
            Parent[i] = open.Count > 0 ? open.Peek() : -1;
            Depth[i] = open.Count;
            if (tokens[i].Kind != TokKind.Punct)
                continue;
            var c = tokens[i].Text[0];
            if (c is '(' or '[' or '{')
            {
                open.Push(i);
                Track(c, 1);
            }
            else if (c is ')' or ']' or '}')
            {
                var wanted = c == ')' ? '(' : c == ']' ? '[' : '{';
                // An unbalanced file: close the nearest opener of this kind, if there is one.
                if ((wanted == '(' ? round : wanted == '[' ? square : curly) == 0)
                    continue;
                while (tokens[open.Peek()].Text[0] != wanted)
                    Track(tokens[open.Pop()].Text[0], -1);
                var opener = open.Pop();
                Track(wanted, -1);
                Match[opener] = i;
                Match[i] = opener;
                Parent[i] = open.Count > 0 ? open.Peek() : -1;
                Depth[i] = open.Count;
            }
        }

        void Track(char opener, int change)
        {
            if (opener == '(') round += change;
            else if (opener == '[') square += change;
            else curly += change;
        }
    }

    /// <summary>For a bracket: the index of its partner, or -1.</summary>
    public int[] Match { get; }
    /// <summary>The index of the innermost open bracket around a token, or -1.</summary>
    public int[] Parent { get; }
    /// <summary>The number of open brackets around a token. A rule uses it to refuse a construct that nests deeper than its documented maximum.</summary>
    public int[] Depth { get; }
    public int Count => _tokens.Count;
    public Tok this[int index] => _tokens[index];

    public static string PunctText(char c) => c < 128 ? Punctuation[c] : c.ToString();

    public bool P(int i, char c) => i >= 0 && i < _tokens.Count && _tokens[i].Kind == TokKind.Punct && _tokens[i].Text[0] == c;
    public bool Id(int i) => i >= 0 && i < _tokens.Count && _tokens[i].Kind == TokKind.Ident;
    public bool Id(int i, string text) => Id(i) && string.Equals(_tokens[i].Text, text, StringComparison.Ordinal);
    public string? IdText(int i) => Id(i) ? _tokens[i].Text : null;
    /// <summary>The value of a string token whose value is known from the text.</summary>
    public string? StaticString(int i) =>
        i >= 0 && i < _tokens.Count && _tokens[i] is { Kind: TokKind.String, IsStatic: true } token ? token.Text : null;
    public int Line(int i) => i >= 0 && i < _tokens.Count ? _tokens[i].Line : 0;
    public int EndLine(int i) => i >= 0 && i < _tokens.Count ? _tokens[i].EndLine : 0;

    /// <summary>The index of the bracket that pairs with the one at <paramref name="i"/>, or -1.</summary>
    public int Close(int i) => i >= 0 && i < _tokens.Count ? Match[i] : -1;

    /// <summary>
    /// The arguments between a matched bracket pair, split at the commas of that level.
    /// Each range is inclusive. Empty when the brackets hold nothing or do not match.
    /// </summary>
    public List<(int Start, int End)> Args(int open)
    {
        var result = new List<(int, int)>();
        var close = Close(open);
        if (close < 0 || close == open + 1)
            return result;
        var start = open + 1;
        for (var i = open + 1; i < close; i++)
        {
            if (Match[i] > i)
            {
                i = Match[i];
                continue;
            }
            if (P(i, ','))
            {
                result.Add((start, i - 1));
                start = i + 1;
            }
        }
        if (start < close)
            result.Add((start, close - 1));
        return result;
    }

    /// <summary>The tokens of a range joined without spaces, as a type name reads.</summary>
    public string Text(int start, int end)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = Math.Max(0, start); i <= end && i < _tokens.Count; i++)
        {
            if (builder.Length > 400)
                break;
            builder.Append(_tokens[i].Text);
        }
        return builder.ToString();
    }

    /// <summary>
    /// For a '&lt;' that opens a type argument list: the index of the '&gt;' that closes it,
    /// or -1 when what follows is not a list of type names.
    /// </summary>
    public int CloseAngle(int open)
    {
        if (!P(open, '<'))
            return -1;
        var depth = 0;
        for (var i = open; i < _tokens.Count && i < open + 64; i++)
        {
            if (P(i, '<'))
                depth++;
            else if (P(i, '>'))
            {
                if (--depth == 0)
                    return i;
            }
            else if (!(Id(i) || P(i, '.') || P(i, ',') || P(i, '?') || P(i, '[') || P(i, ']')))
                return -1;
        }
        return -1;
    }
}
