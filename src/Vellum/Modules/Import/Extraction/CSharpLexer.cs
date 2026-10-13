using System.Text;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// The tokens of one C# file, and for each token the preprocessor condition it stands under.
/// The condition is kept beside the tokens and not in <see cref="Tok"/>.
/// </summary>
internal sealed class CSharpSource(TokenList tokens, int[] conditionOf, List<string> conditions, List<(string Reason, int Line)> notes)
{
    public TokenList Tokens { get; } = tokens;

    /// <summary>Fixed reason codes with the line of the directive they are about.</summary>
    public IReadOnlyList<(string Reason, int Line)> Notes { get; } = notes;

    /// <summary>
    /// The condition under which the token is compiled, as an expression over the symbols the scan
    /// cannot evaluate (<c>DEBUG</c>, <c>!DEBUG &amp;&amp; TRACE</c>), or null for a token that is always compiled.
    /// </summary>
    public string? Conditional(int token) =>
        token >= 0 && token < conditionOf.Length && conditionOf[token] > 0 ? conditions[conditionOf[token]] : null;
}

/// <summary>
/// A tokenizer for C# source: identifiers, numbers, punctuation, character literals and every
/// string form (regular, verbatim, interpolated, raw). Comments give no tokens. It does not
/// parse and it never fails. Every character is read a bounded number of times, and the
/// <see cref="WorkMeter"/> of the file is charged for each pass.
/// <para>Preprocessor: <c>#if</c>, <c>#elif</c>, <c>#else</c> and <c>#endif</c> are evaluated
/// with three values. <c>true</c> and <c>false</c> are known; every symbol is unknown, because
/// the scan does not know the build configuration (<c>#define</c> and <c>#undef</c> are not read).
/// The operators are <c>!</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>==</c>, <c>!=</c> and parentheses.</para>
/// <list type="bullet">
/// <item>A branch that is certainly not compiled gives no tokens. Its text is not tokenized, as
/// in the compiler: only the directives in it are read.</item>
/// <item>A branch whose condition is unknown gives tokens that carry the condition
/// (<see cref="CSharpSource.Conditional"/>): its own expression, the negation of the earlier
/// branches of the group, and the condition of the enclosing group, joined with <c>&amp;&amp;</c>.</item>
/// <item>A branch whose whole condition is a symbol spelled like <c>false</c> in another case
/// (<c>FALSE</c>, <c>False</c>) gives no tokens, because it is written to switch code off. It is
/// still a symbol: the later branches of its group are conditional on <c>!FALSE</c>.</item>
/// <item>A condition that is not such an expression, is longer than <see cref="MaxConditionLength"/>
/// characters with its context, or has the shape of a credential assignment gives no tokens for
/// that branch and the later branches of its group, and a note.</item>
/// <item>Groups nest to <see cref="MaxConditionalDepth"/>. A deeper group gives no tokens, and a note.</item>
/// </list>
/// Other directives (<c>#region</c>, <c>#pragma</c>, <c>#error</c>, <c>#line</c>, <c>#nullable</c>) give nothing.
/// </summary>
internal sealed class CSharpLexer
{
    // A hole of an interpolated string can hold a string with a hole. Deeper than this, the
    // rest of the line is dropped instead of recursing.
    private const int MaxStringDepth = 16;
    /// <summary>The deepest nesting of <c>#if</c> groups that is read.</summary>
    public const int MaxConditionalDepth = 32;
    /// <summary>The longest condition, with the conditions around it, that a token can carry.</summary>
    public const int MaxConditionLength = 200;
    private const int MaxExpressionDepth = 8;

    private const int Inactive = -1;
    private const int Unconditional = 0;

    private enum Truth { False, True, Unknown, FalseSymbol, Unreadable }

    private struct Group
    {
        /// <summary>The condition of the code around the group: <see cref="Inactive"/>, <see cref="Unconditional"/> or an index of a condition.</summary>
        public int Outer;
        /// <summary>A branch whose condition is certainly true was seen: the later branches are not compiled.</summary>
        public bool Taken;
        /// <summary>A branch could not be read: nothing is known about the later branches.</summary>
        public bool Unreadable;
        /// <summary>The negations of the earlier branches with an unknown condition, joined with "&amp;&amp;".</summary>
        public string Negations;
    }

    private readonly string _s;
    private readonly WorkMeter _meter;
    private readonly List<Tok> _tokens = [];
    private readonly List<int> _conditionOf = [];
    private readonly List<string> _conditions = [""];
    private readonly Dictionary<string, int> _conditionIndex = new(StringComparer.Ordinal);
    private readonly List<(string Reason, int Line)> _notes = [];
    private readonly List<Group> _groups = [];
    private int _skippedGroups;
    private int _beforeSkipped;
    private int _current = Unconditional;

    private CSharpLexer(string s, WorkMeter meter)
    {
        _s = s;
        _meter = meter;
    }

    /// <summary>The tokens read before the work budget of the file was spent. The caller checks the meter.</summary>
    public static CSharpSource Tokenize(string s, WorkMeter meter)
    {
        var lexer = new CSharpLexer(s, meter);
        // One pass: every character is consumed once by the loop or by the string it starts.
        if (meter.Charge(s.Length + 1))
            lexer.Run();
        var tokens = new TokenList(lexer._tokens);
        meter.Charge(lexer._tokens.Count);
        return new CSharpSource(tokens, [.. lexer._conditionOf], lexer._conditions, lexer._notes);
    }

    private void Add(Tok token)
    {
        _tokens.Add(token);
        _conditionOf.Add(_current);
    }

    private void Run()
    {
        var s = _s;
        var builder = new StringBuilder();
        int i = 0, line = 1, n = s.Length;
        var lineStart = true;
        while (i < n && !_meter.Exhausted)
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
            if (c == '#' && lineStart)
            {
                var end = s.IndexOf('\n', i);
                if (end < 0)
                    end = n;
                Directive(s.AsSpan(i + 1, end - i - 1), line);
                i = end;
                continue;
            }
            if (_current == Inactive)
            {
                // Text that is not compiled is not tokenized: only its directives are read.
                var end = s.IndexOf('\n', i);
                i = end < 0 ? n : end;
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? n : end;
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
            lineStart = false;

            var startLine = line;
            if (c is '"' or '$' or '@')
            {
                builder.Clear();
                var after = TryString(i, ref line, builder, out var dynamic, 0);
                if (after > i)
                {
                    Add(new Tok(TokKind.String, builder.ToString(), startLine, line, !dynamic));
                    i = after;
                    continue;
                }
                if (c == '$')
                {
                    // No string starts at any '$' of this run: the run is read once and not again.
                    while (i < n && s[i] == '$')
                    {
                        Add(new Tok(TokKind.Punct, "$", line, line, true));
                        i++;
                    }
                    continue;
                }
            }
            if (c == '\'')
            {
                var after = SkipChar(i);
                if (after > i + 1)
                {
                    Add(new Tok(TokKind.Char, "", line, line, true));
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
                Add(new Tok(TokKind.Ident, s[start..i], line, line, true));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || (s[i] == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1]))))
                    i++;
                Add(new Tok(TokKind.Number, s[start..i], line, line, true));
                continue;
            }
            Add(new Tok(TokKind.Punct, TokenList.PunctText(c), line, line, true));
            i++;
        }
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    // ---- preprocessor -----------------------------------------------------------------

    private void Directive(ReadOnlySpan<char> text, int line)
    {
        text = text.TrimStart();
        var length = 0;
        while (length < text.Length && char.IsAsciiLetter(text[length]))
            length++;
        var keyword = text[..length];
        var rest = text[length..];
        var comment = rest.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
            rest = rest[..comment];
        rest = rest.Trim();

        if (keyword.SequenceEqual("if"))
        {
            if (_skippedGroups > 0 || _groups.Count >= MaxConditionalDepth)
            {
                if (_skippedGroups++ == 0)
                {
                    _beforeSkipped = _current;
                    _current = Inactive;
                    _notes.Add(("preprocessor-nesting", line));
                }
                return;
            }
            _groups.Add(new Group { Outer = _current, Negations = "" });
            Branch(Evaluate(rest, out var expression), expression, line);
        }
        else if (keyword.SequenceEqual("elif"))
        {
            if (_skippedGroups != 0 || _groups.Count == 0)
                return;
            // The condition of a branch that is not compiled whatever it says is not read.
            var group = _groups[^1];
            if (group.Outer == Inactive || group.Taken || group.Unreadable)
                _current = Inactive;
            else
                Branch(Evaluate(rest, out var expression), expression, line);
        }
        else if (keyword.SequenceEqual("else"))
        {
            if (_skippedGroups == 0 && _groups.Count > 0)
                Branch(Truth.True, "", line);
        }
        else if (keyword.SequenceEqual("endif"))
        {
            if (_skippedGroups > 0)
            {
                if (--_skippedGroups == 0)
                    _current = _beforeSkipped;
            }
            else if (_groups.Count > 0)
            {
                _current = _groups[^1].Outer;
                _groups.RemoveAt(_groups.Count - 1);
            }
        }
    }

    private void Branch(Truth truth, string expression, int line)
    {
        var group = _groups[^1];
        if (group.Outer == Inactive || group.Taken || group.Unreadable)
        {
            _current = Inactive;
            return;
        }
        switch (truth)
        {
            case Truth.False:
                _current = Inactive;
                break;
            case Truth.FalseSymbol:
                _current = Inactive;
                group.Negations = And(group.Negations, "!" + expression);
                break;
            case Truth.True:
                group.Taken = true;
                _current = group.Negations.Length == 0 ? group.Outer : Condition(group.Outer, group.Negations);
                break;
            case Truth.Unknown:
                _current = Condition(group.Outer, And(group.Negations, expression));
                group.Negations = And(group.Negations, IsSymbol(expression) ? "!" + expression : "!(" + expression + ")");
                break;
            default:
                _current = Inactive;
                break;
        }
        if (truth == Truth.Unreadable || _current == Inactive && truth is Truth.True or Truth.Unknown || group.Negations.Length > MaxConditionLength)
        {
            // Nothing can be said about this branch, and so nothing about the branches after it.
            group.Unreadable = true;
            group.Negations = "";
            _current = Inactive;
            _notes.Add(("preprocessor-condition-unreadable", line));
        }
        _groups[^1] = group;
    }

    /// <summary>The index of a condition under the condition around it, or <see cref="Inactive"/> when it is too long.</summary>
    private int Condition(int outer, string text)
    {
        var full = outer == Unconditional ? text : And(_conditions[outer], text);
        _meter.Charge(full.Length);
        if (full.Length > MaxConditionLength)
            return Inactive;
        if (!_conditionIndex.TryGetValue(full, out var index))
        {
            index = _conditions.Count;
            _conditions.Add(full);
            _conditionIndex[full] = index;
        }
        return index;
    }

    private static string And(string left, string right)
    {
        if (right.Contains("||", StringComparison.Ordinal) && !IsParenthesized(right))
            right = "(" + right + ")";
        return left.Length == 0 ? right : left + " && " + right;
    }

    private static bool IsSymbol(string expression)
    {
        foreach (var c in expression)
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        return expression.Length > 0;
    }

    /// <summary>True when the first parenthesis closes at the last character.</summary>
    private static bool IsParenthesized(string expression)
    {
        if (expression.Length < 2 || expression[0] != '(')
            return false;
        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')' && --depth == 0)
                return i == expression.Length - 1;
        }
        return false;
    }

    /// <summary>
    /// The value of a preprocessor expression, and its text with one spelling for the spaces.
    /// </summary>
    private Truth Evaluate(ReadOnlySpan<char> text, out string expression)
    {
        expression = "";
        if (text.Length == 0 || text.Length > MaxConditionLength)
            return Truth.Unreadable;
        // The text is read here, and again by the credential check on its normal form.
        _meter.Charge(4L * text.Length + 64);

        var parts = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c is ' ' or '\t')
            {
                i++;
            }
            else if (IsIdentStart(c))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                parts.Add(text[start..i].ToString());
            }
            else if (i + 1 < text.Length && (c is '&' && text[i + 1] == '&' || c is '|' && text[i + 1] == '|' || c is '=' or '!' && text[i + 1] == '='))
            {
                parts.Add(text.Slice(i, 2).ToString());
                i += 2;
            }
            else if (c is '!' or '(' or ')')
            {
                parts.Add(TokenList.PunctText(c));
                i++;
            }
            else
            {
                return Truth.Unreadable;
            }
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var part in parts)
        {
            if (part is "&&" or "||" or "==" or "!=")
                builder.Append(' ').Append(part).Append(' ');
            else
                builder.Append(part);
        }
        expression = builder.ToString();
        if (CredentialGuard.LooksSecret(expression))
            return Truth.Unreadable;

        var position = 0;
        var failed = false;
        var value = Or(parts, ref position, ref failed, 0);
        if (failed || position != parts.Count)
            return Truth.Unreadable;
        if (parts.Count == 1 && parts[0] != "false" && parts[0].Equals("false", StringComparison.OrdinalIgnoreCase))
            return Truth.FalseSymbol;
        return value;
    }

    private static Truth Or(List<string> parts, ref int position, ref bool failed, int depth)
    {
        var left = AndTerm(parts, ref position, ref failed, depth);
        while (!failed && position < parts.Count && parts[position] == "||")
        {
            position++;
            var right = AndTerm(parts, ref position, ref failed, depth);
            left = left == Truth.True || right == Truth.True ? Truth.True
                : left == Truth.False && right == Truth.False ? Truth.False : Truth.Unknown;
        }
        return left;
    }

    private static Truth AndTerm(List<string> parts, ref int position, ref bool failed, int depth)
    {
        var left = Equality(parts, ref position, ref failed, depth);
        while (!failed && position < parts.Count && parts[position] == "&&")
        {
            position++;
            var right = Equality(parts, ref position, ref failed, depth);
            left = left == Truth.False || right == Truth.False ? Truth.False
                : left == Truth.True && right == Truth.True ? Truth.True : Truth.Unknown;
        }
        return left;
    }

    private static Truth Equality(List<string> parts, ref int position, ref bool failed, int depth)
    {
        var left = Unary(parts, ref position, ref failed, depth);
        while (!failed && position < parts.Count && parts[position] is "==" or "!=")
        {
            var same = parts[position++] == "==";
            var right = Unary(parts, ref position, ref failed, depth);
            left = left == Truth.Unknown || right == Truth.Unknown ? Truth.Unknown
                : (left == right) == same ? Truth.True : Truth.False;
        }
        return left;
    }

    private static Truth Unary(List<string> parts, ref int position, ref bool failed, int depth)
    {
        // The depth counts "!" and "(": the recursion is as deep as the expression is nested, and no deeper than the bound.
        if (failed || position >= parts.Count || depth > MaxExpressionDepth)
        {
            failed = true;
            return Truth.Unknown;
        }
        var part = parts[position++];
        switch (part)
        {
            case "!":
                var operand = Unary(parts, ref position, ref failed, depth + 1);
                return operand == Truth.True ? Truth.False : operand == Truth.False ? Truth.True : Truth.Unknown;
            case "(":
                var inner = Or(parts, ref position, ref failed, depth + 1);
                if (position >= parts.Count || parts[position] != ")")
                    failed = true;
                position++;
                return inner;
            case "true":
                return Truth.True;
            case "false":
                return Truth.False;
            default:
                if (!IsIdentStart(part[0]))
                    failed = true;
                return Truth.Unknown;
        }
    }

    // ---- strings ----------------------------------------------------------------------

    /// <summary>
    /// Reads a string literal that starts at <paramref name="i"/>. Returns the index after
    /// it, or <paramref name="i"/> when no string starts there. In that case no string starts
    /// at any other '$' of the run at <paramref name="i"/> either, so the caller steps over the run.
    /// </summary>
    private int TryString(int i, ref int line, StringBuilder? value, out bool dynamic, int depth)
    {
        var s = _s;
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
        {
            // The prefix was read and gave nothing.
            _meter.Charge(j - i);
            return i;
        }

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
        if (quotes > 2)
            _meter.Charge(quotes);

        return ScanString(j + 1, ref line, verbatim, dollars > 0, value, ref dynamic, depth);
    }

    private int ScanString(int i, ref int line, bool verbatim, bool interpolated, StringBuilder? value, ref bool dynamic, int depth)
    {
        var s = _s;
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
                i = Escape(i, value);
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
                i = SkipHole(i + 1, ref line, depth);
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
    private int SkipHole(int i, ref int line, int depth)
    {
        var s = _s;
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
                i = Math.Max(i + 1, SkipChar(i));
            }
            else if (c is '"' or '$' or '@')
            {
                if (depth >= MaxStringDepth)
                {
                    while (i < n && s[i] != '\n')
                        i++;
                    return i;
                }
                var after = TryString(i, ref line, null, out _, depth + 1);
                if (after > i)
                {
                    i = after;
                }
                else
                {
                    // No string starts in this run of '$'; it is not read again.
                    i++;
                    while (c == '$' && i < n && s[i] == '$')
                        i++;
                }
            }
            else
            {
                i++;
            }
        }
        return i;
    }

    private int Escape(int i, StringBuilder? value)
    {
        var s = _s;
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
    private int SkipChar(int i)
    {
        var s = _s;
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
