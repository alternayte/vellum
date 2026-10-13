using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// A tokenizer for C# source: identifiers, numbers, punctuation, character literals and every
/// string form (regular, verbatim, interpolated, raw). Comments and preprocessor lines give
/// no tokens. It does not parse; it never fails; its work is linear in the input.
/// Code in an inactive <c>#if</c> branch is tokenized like any other code.
/// </summary>
internal static class CSharpLexer
{
    // A hole of an interpolated string can hold a string with a hole. Deeper than this, the
    // rest of the line is dropped instead of recursing.
    private const int MaxStringDepth = 16;

    public static TokenList Tokenize(string s)
    {
        var tokens = new List<Tok>();
        var builder = new StringBuilder();
        int i = 0, line = 1, n = s.Length;
        var lineStart = true;
        while (i < n)
        {
            var c = s[i];
            if (c == '\n')
            {
                line++;
                i++;
                lineStart = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
                {
                    if (s[i] == '\n')
                        line++;
                    i++;
                }
                i = Math.Min(n, i + 2);
                continue;
            }
            if (c == '#' && lineStart)
            {
                while (i < n && s[i] != '\n')
                    i++;
                continue;
            }
            lineStart = false;

            var startLine = line;
            if (c is '"' or '$' or '@')
            {
                builder.Clear();
                var after = TryString(s, i, ref line, builder, out var dynamic, 0);
                if (after > i)
                {
                    tokens.Add(new Tok(TokKind.String, builder.ToString(), startLine, line, !dynamic));
                    i = after;
                    continue;
                }
            }
            if (c == '\'')
            {
                var after = SkipChar(s, i);
                if (after > i + 1)
                {
                    tokens.Add(new Tok(TokKind.Char, "", line, line, true));
                    i = after;
                    continue;
                }
            }
            if (c == '@' && i + 1 < n && IsIdentStart(s[i + 1]))
                i++;
            if (IsIdentStart(s[i]))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    i++;
                tokens.Add(new Tok(TokKind.Ident, s[start..i], line, line, true));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || (s[i] == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1]))))
                    i++;
                tokens.Add(new Tok(TokKind.Number, s[start..i], line, line, true));
                continue;
            }
            tokens.Add(new Tok(TokKind.Punct, TokenList.PunctText(c), line, line, true));
            i++;
        }
        return new TokenList(tokens);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>
    /// Reads a string literal that starts at <paramref name="i"/>. Returns the index after
    /// it, or <paramref name="i"/> when no string starts there.
    /// </summary>
    private static int TryString(string s, int i, ref int line, StringBuilder? value, out bool dynamic, int depth)
    {
        dynamic = false;
        int n = s.Length, j = i, dollars = 0;
        var verbatim = false;
        while (j < n && s[j] == '$')
        {
            dollars++;
            j++;
        }
        if (j < n && s[j] == '@')
        {
            verbatim = true;
            j++;
            while (j < n && s[j] == '$')
            {
                dollars++;
                j++;
            }
        }
        if (j >= n || s[j] != '"')
            return i;

        var quotes = 0;
        while (j + quotes < n && s[j + quotes] == '"')
            quotes++;
        if (quotes >= 3 && !verbatim)
        {
            // Raw string: ends at the same number of quotes.
            var start = j + quotes;
            var k = start;
            while (k < n)
            {
                if (s[k] == '"')
                {
                    var run = 0;
                    while (k + run < n && s[k + run] == '"')
                        run++;
                    if (run >= quotes)
                    {
                        value?.Append(s, start, k + run - quotes - start);
                        dynamic = dollars > 0 && s.AsSpan(start, k - start).Contains('{');
                        return k + run;
                    }
                    k += run;
                    continue;
                }
                if (s[k] == '\n')
                    line++;
                k++;
            }
            value?.Append(s, start, n - start);
            dynamic = dollars > 0;
            return n;
        }

        return ScanString(s, j + 1, ref line, verbatim, dollars > 0, value, ref dynamic, depth);
    }

    private static int ScanString(string s, int i, ref int line, bool verbatim, bool interpolated, StringBuilder? value, ref bool dynamic, int depth)
    {
        var n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (c == '"')
            {
                if (verbatim && i + 1 < n && s[i + 1] == '"')
                {
                    value?.Append('"');
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            if (c == '\n')
            {
                // A regular string that is not closed ends with its line.
                if (!verbatim)
                    return i;
                line++;
                value?.Append(c);
                i++;
                continue;
            }
            if (c == '\\' && !verbatim)
            {
                i = Escape(s, i, value);
                continue;
            }
            if (interpolated && c == '{')
            {
                if (i + 1 < n && s[i + 1] == '{')
                {
                    value?.Append('{');
                    i += 2;
                    continue;
                }
                dynamic = true;
                i = SkipHole(s, i + 1, ref line, depth);
                continue;
            }
            if (interpolated && c == '}' && i + 1 < n && s[i + 1] == '}')
            {
                value?.Append('}');
                i += 2;
                continue;
            }
            value?.Append(c);
            i++;
        }
        return i;
    }

    /// <summary>Skips the expression of an interpolation hole, to the index after its closing brace.</summary>
    private static int SkipHole(string s, int i, ref int line, int depth)
    {
        var n = s.Length;
        var braces = 0;
        while (i < n)
        {
            var c = s[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '{')
            {
                braces++;
                i++;
            }
            else if (c == '}')
            {
                i++;
                if (braces-- == 0)
                    return i;
            }
            else if (c == '\'')
            {
                i = Math.Max(i + 1, SkipChar(s, i));
            }
            else if (c is '"' or '$' or '@')
            {
                if (depth >= MaxStringDepth)
                {
                    while (i < n && s[i] != '\n')
                        i++;
                    return i;
                }
                var after = TryString(s, i, ref line, null, out _, depth + 1);
                i = after > i ? after : i + 1;
            }
            else
            {
                i++;
            }
        }
        return i;
    }

    private static int Escape(string s, int i, StringBuilder? value)
    {
        if (i + 1 >= s.Length)
            return i + 1;
        var c = s[i + 1];
        switch (c)
        {
            case 'n': value?.Append('\n'); return i + 2;
            case 't': value?.Append('\t'); return i + 2;
            case 'r': value?.Append('\r'); return i + 2;
            case '0': value?.Append('\0'); return i + 2;
            case '\n': return i + 1;
            case 'u' when i + 5 < s.Length && TryHex(s.AsSpan(i + 2, 4), out var unit):
                value?.Append((char)unit);
                return i + 6;
            default: value?.Append(c); return i + 2;
        }
    }

    private static bool TryHex(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>The index after a character literal at <paramref name="i"/>, or i + 1 when there is none.</summary>
    private static int SkipChar(string s, int i)
    {
        var n = s.Length;
        if (i + 2 < n && s[i + 1] != '\\' && s[i + 1] != '\n' && s[i + 2] == '\'')
            return i + 3;
        if (i + 1 < n && s[i + 1] == '\\')
        {
            for (var k = i + 3; k < n && k <= i + 12 && s[k] != '\n'; k++)
                if (s[k] == '\'')
                    return k + 1;
        }
        return i + 1;
    }
}
