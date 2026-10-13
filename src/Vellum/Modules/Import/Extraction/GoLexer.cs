using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// A tokenizer for Go source: identifiers, numbers, punctuation, rune literals, interpreted
/// and raw strings. Comments give no tokens. It does not parse; it never fails; its work is
/// linear in the input.
/// </summary>
internal static class GoLexer
{
    public static TokenList Tokenize(string s)
    {
        var tokens = new List<Tok>();
        var builder = new StringBuilder();
        int i = 0, line = 1, n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (c == '\n')
            {
                line++;
                i++;
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

            var startLine = line;
            if (c == '`')
            {
                var start = ++i;
                while (i < n && s[i] != '`')
                {
                    if (s[i] == '\n')
                        line++;
                    i++;
                }
                tokens.Add(new Tok(TokKind.String, s[start..i], startLine, line, true));
                i = Math.Min(n, i + 1);
                continue;
            }
            if (c == '"')
            {
                builder.Clear();
                i++;
                while (i < n && s[i] != '"' && s[i] != '\n')
                {
                    if (s[i] == '\\' && i + 1 < n && s[i + 1] != '\n')
                    {
                        builder.Append(s[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
                        i += 2;
                    }
                    else
                    {
                        builder.Append(s[i++]);
                    }
                }
                if (i < n && s[i] == '"')
                    i++;
                tokens.Add(new Tok(TokKind.String, builder.ToString(), startLine, startLine, true));
                continue;
            }
            if (c == '\'')
            {
                // A rune: 'x', an escape such as '\n', or one code point.
                var k = i + 1;
                if (k < n && s[k] == '\\')
                    k += 2;
                else if (k < n && s[k] != '\n')
                    k += char.IsHighSurrogate(s[k]) ? 2 : 1;
                while (k < n && k <= i + 12 && s[k] != '\'' && s[k] != '\n')
                    k++;
                if (k < n && s[k] == '\'')
                {
                    tokens.Add(new Tok(TokKind.Char, "", line, line, true));
                    i = k + 1;
                    continue;
                }
            }
            if (char.IsLetter(c) || c == '_')
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
}
