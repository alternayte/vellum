namespace Vellum.Modules.Import.Extraction;

internal sealed record CSharpAttribute(string Name, int ArgsOpen, int Line);

/// <param name="Name">The last identifier of the written type name.</param>
/// <param name="Qualifier">What is written before the last dot, or "".</param>
/// <param name="ArgsStart">The first token of the type arguments, or -1.</param>
internal sealed record CSharpBase(string Name, string Qualifier, int ArgsStart, int ArgsEnd, int ArgCount, int Line);

internal sealed class CSharpType
{
    public required string Keyword { get; init; }
    public required string Name { get; init; }
    /// <summary>The namespace and the enclosing types, joined with dots. "" for the global namespace.</summary>
    public required string Container { get; init; }
    public required int KeywordIndex { get; init; }
    /// <summary>The first attribute or modifier.</summary>
    public required int StartIndex { get; init; }
    public required int ParametersOpen { get; init; }
    public required int BodyOpen { get; init; }
    public required int EndIndex { get; init; }
    public bool IsPartial { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsStatic { get; init; }
    public List<CSharpAttribute> Attributes { get; } = [];
    public List<CSharpBase> Bases { get; } = [];
    public List<int> TypeParameters { get; } = [];
}

internal enum CSharpDeclKind { Variable, LambdaParameter, TypeParameter }

/// <summary>A name that the source declares, and the tokens it is in scope for.</summary>
internal sealed class CSharpDecl(CSharpDeclKind kind, int nameIndex, int end, int typeIndex)
{
    public CSharpDeclKind Kind { get; } = kind;
    public int NameIndex { get; } = nameIndex;
    /// <summary>The last token of the scope.</summary>
    public int End { get; } = end;
    /// <summary>The innermost type declaration around the scope, or -1 for top-level statements.</summary>
    public int TypeIndex { get; } = typeIndex;
    /// <summary>The last identifier of the declared type. Null for <c>var</c>, an array, an untyped lambda parameter.</summary>
    public string? TypeName { get; set; }
    /// <summary>The one type argument of the declared type when it is a plain name: "ShopDb" of <c>IDbContextFactory&lt;ShopDb&gt;</c>.</summary>
    public string? TypeArgument { get; set; }
    public bool IsVar { get; set; }
    public bool IsArray { get; set; }
    /// <summary>The name tokens of "name = …" that assign this declaration, the declaration itself first.</summary>
    public List<int>? Assignments { get; set; }
}

/// <summary>
/// What one C# file declares, read from its tokens without a parser: namespaces, using
/// directives, type declarations, and the scope of every declared name.
/// <para>Scopes. A name is looked up at a token as the innermost declaration of that name whose
/// scope holds the token:</para>
/// <list type="bullet">
/// <item>a parameter (typed, in a parameter list that is followed by a body or by <c>=&gt;</c>): the body, or the expression up to its <c>;</c>;</item>
/// <item>a lambda parameter (<c>x =&gt; …</c>, <c>(x, y) =&gt; …</c>): the block, or up to the next <c>;</c> of that
/// bracket level or the closing bracket around it, which can be later than the end of the lambda (the name is then hidden for longer, never shorter);</item>
/// <item>a primary constructor parameter and a type parameter of a type: the type body; a type parameter of a method: its body;</item>
/// <item>a local, a field, a property, an <c>out</c> or pattern variable (two names in a row, <c>Type name</c>, that
/// are not a method header; <c>var (a, b)</c>): from the declaration to the end of the enclosing braces, or of the file for top-level statements;</item>
/// <item>a parameter of a declaration without a body (abstract, interface, delegate): its own parameter list only.</item>
/// </list>
/// A declaration is visible only in the type declaration it stands in (a type parameter also in
/// nested types). So a top-level variable is not seen by a method of a class in the same file,
/// and a field of one class is not seen by another class.
/// <para>Not read: the second and later declarators of one declaration, deconstruction into typed
/// names, <c>out</c> and pattern variables of a generic or array type, query range variables,
/// members inherited or declared in another part of a partial type. A name with no visible
/// declaration is unknown to the rules.</para>
/// <para>Every pass is linear in the tokens and charged to the meter. Types nest to
/// <see cref="MaxNesting"/> with namespaces; deeper declarations are noted and not read.</para>
/// </summary>
internal sealed class CSharpFile
{
    /// <summary>The deepest nesting of namespaces and types that is read.</summary>
    public const int MaxNesting = 8;
    /// <summary>The longest namespace-qualified name. A longer one is noted and not read.</summary>
    public const int MaxNameLength = 256;
    private const int MaxHeaderTokens = 64;
    private const int MaxAngleTokens = 32;

    private static readonly HashSet<string> TypeModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly",
        "unsafe", "new", "file", "ref", "record",
    };

    /// <summary>Words that stand before a name without being its type.</summary>
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal)
    {
        "return", "new", "await", "throw", "yield", "in", "out", "ref", "is", "as", "case", "goto", "else", "do",
        "typeof", "nameof", "sizeof", "default", "using", "this", "base", "null", "true", "false", "if", "while",
        "for", "foreach", "switch", "lock", "try", "catch", "finally", "break", "continue", "checked", "unchecked",
        "fixed", "stackalloc", "when", "where", "select", "from", "let", "join", "on", "equals", "by", "group",
        "into", "orderby", "ascending", "descending", "not", "and", "or", "with", "operator", "implicit", "explicit",
        "params", "event", "delegate", "namespace", "class", "struct", "interface", "enum", "record", "public",
        "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly", "unsafe", "file",
        "virtual", "override", "async", "extern", "required", "const", "volatile", "get", "set", "init", "add",
        "remove", "global", "scoped",
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "foreach", "switch", "using", "lock", "catch", "fixed", "when",
    };

    private readonly TokenList t;
    private readonly WorkMeter _meter;
    private readonly int[] _nextSemicolon;
    private readonly int[] _nextComma;
    private readonly int[] _enclosingBrace;
    private readonly int[] _typeOf;
    private readonly int[] _declOf;
    private readonly List<CSharpDecl> _decls = [];
    private readonly Func<string, bool> _wantsAttribute;

    public List<CSharpType> Types { get; } = [];
    public HashSet<string> Usings { get; } = new(StringComparer.Ordinal);
    public HashSet<string> GlobalUsings { get; } = new(StringComparer.Ordinal);
    /// <summary>Names of <c>using X = …;</c>. A written name that is an alias is not looked up as a type.</summary>
    public HashSet<string> Aliases { get; } = new(StringComparer.Ordinal);
    /// <summary>The file-scoped namespace, or "".</summary>
    public string FileNamespace { get; private set; } = "";
    public List<(string Reason, int Line)> Notes { get; } = [];
    /// <summary>False when the work budget was spent: the file is not read further.</summary>
    public bool IsComplete { get; }

    /// <param name="wantsAttribute">The attribute names (without "Attribute") that the caller reads. Others are not kept.</param>
    public CSharpFile(TokenList tokens, WorkMeter meter, Func<string, bool> wantsAttribute)
    {
        t = tokens;
        _meter = meter;
        _wantsAttribute = wantsAttribute;
        var n = t.Count;
        _nextSemicolon = new int[n];
        _nextComma = new int[n];
        _enclosingBrace = new int[n];
        _typeOf = new int[n];
        _declOf = new int[n];
        IsComplete = meter.Charge(4L * n) && ReadLevels() && ReadTypes() && ReadTypeOf() && ReadScopes();
    }

    public int TypeOf(int token) => token >= 0 && token < _typeOf.Length ? _typeOf[token] : -1;

    /// <summary>The declaration that a name token refers to, or null when none is visible there.</summary>
    public CSharpDecl? DeclAt(int token) =>
        token >= 0 && token < _declOf.Length && _declOf[token] >= 0 ? _decls[_declOf[token]] : null;

    /// <summary>The next ',' of the bracket level of a token, or -1.</summary>
    public int NextComma(int token) => token >= 0 && token < _nextComma.Length ? _nextComma[token] : -1;

    /// <summary>
    /// The last token of the expression of "name = expression;", or -1 when the statement does
    /// not end with a ';' of its level or a ',' of that level stands before it (a second
    /// declarator, an argument, a type argument list: the end of the expression is then not known).
    /// </summary>
    public int ExpressionEnd(int nameIndex)
    {
        if (nameIndex < 0 || nameIndex >= _nextSemicolon.Length)
            return -1;
        var semicolon = _nextSemicolon[nameIndex];
        var comma = _nextComma[nameIndex];
        return semicolon < 0 || (comma >= 0 && comma < semicolon) ? -1 : semicolon - 1;
    }

    /// <summary>For a '&gt;' that closes a type argument list: the index of its '&lt;', or -1. It looks back a bounded number of tokens.</summary>
    public int AngleOpen(int close)
    {
        if (!t.P(close, '>'))
            return -1;
        var depth = 0;
        for (var k = close; k > 0 && k > close - MaxAngleTokens; k--)
        {
            if (!_meter.Charge())
                return -1;
            if (t.P(k, '>'))
                depth++;
            else if (t.P(k, '<'))
            {
                if (--depth == 0)
                    return k;
            }
            else if (!(t.Id(k) || t.P(k, '.') || t.P(k, ',') || t.P(k, '?') || t.P(k, '[') || t.P(k, ']')))
                return -1;
        }
        return -1;
    }

    /// <summary><see cref="TokenList.CloseAngle"/>, charged for the tokens it can read.</summary>
    public int CloseAngle(int open) => t.P(open, '<') && _meter.Charge(MaxHeaderTokens) ? t.CloseAngle(open) : -1;

    /// <summary>"A.B.C" for a range that is identifiers joined with dots and no longer than <see cref="MaxNameLength"/>; else null.</summary>
    public string? DottedName(int start, int end)
    {
        if (start < 0 || end < start || end >= t.Count || (end - start) % 2 != 0)
            return null;
        var length = 0;
        for (var k = start; k <= end; k++)
        {
            if (!_meter.Charge())
                return null;
            if ((k - start) % 2 == 0 ? !t.Id(k) : !t.P(k, '.'))
                return null;
            length += t[k].Text.Length;
            if (length > MaxNameLength)
                return null;
        }
        return start == end ? t[start].Text : t.Text(start, end);
    }

    /// <summary>The attributes of one "[…]" group that the caller reads.</summary>
    public void ReadAttributeGroup(int open, List<CSharpAttribute> attributes)
    {
        var close = t.Match[open];
        var s = open + 1;
        while (s < close)
        {
            if (!_meter.Charge())
                return;
            var k = s;
            // A target: "[return: …]", "[method: …]".
            if (t.Id(k) && t.P(k + 1, ':'))
                k += 2;
            while (t.Id(k) && t.P(k + 1, '.') && t.Id(k + 2) && _meter.Charge())
                k += 2;
            if (t.Id(k) && k < close)
            {
                var name = t[k].Text;
                if (name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal))
                    name = name[..^"Attribute".Length];
                if (_wantsAttribute(name))
                    attributes.Add(new CSharpAttribute(name, t.P(k + 1, '(') && t.Match[k + 1] > k ? k + 1 : -1, t.Line(k)));
            }
            // To the next ',' of the group.
            while (k < close && !t.P(k, ',') && _meter.Charge())
                k = t.Match[k] > k ? t.Match[k] + 1 : k + 1;
            s = k + 1;
        }
    }

    // ---- bracket levels ---------------------------------------------------------------

    private bool ReadLevels()
    {
        var n = t.Count;
        for (var i = 0; i < n; i++)
        {
            var parent = t.Parent[i];
            _enclosingBrace[i] = parent < 0 ? -1 : t.P(parent, '{') ? parent : _enclosingBrace[parent];
            _typeOf[i] = -1;
            _declOf[i] = -1;
        }
        // The last ';' and ',' seen at each bracket level, by the opening bracket of the level.
        var lastSemicolon = new int[n + 1];
        var lastComma = new int[n + 1];
        Array.Fill(lastSemicolon, -1);
        Array.Fill(lastComma, -1);
        for (var i = n - 1; i >= 0; i--)
        {
            var level = t.Parent[i] + 1;
            _nextSemicolon[i] = lastSemicolon[level];
            _nextComma[i] = lastComma[level];
            if (t.P(i, ';'))
                lastSemicolon[level] = i;
            else if (t.P(i, ','))
                lastComma[level] = i;
        }
        return true;
    }

    // ---- namespaces, usings and types -------------------------------------------------

    private bool ReadTypes()
    {
        // The namespaces and types around the current token: the last token of each, and its qualified name.
        var scopes = new List<(int End, string? Name)>();
        var unreadUntil = -1;
        string? fileNamespace = "";
        for (var i = 0; i < t.Count; i++)
        {
            if (!t.Id(i))
                continue;
            var keyword = t[i].Text;
            if (keyword == "using")
            {
                if (!ReadUsing(i))
                    return false;
                continue;
            }
            var isNamespace = keyword == "namespace";
            if (!isNamespace && keyword is not ("class" or "record" or "interface" or "struct"))
                continue;
            if (!_meter.Charge())
                return false;
            while (scopes.Count > 0 && scopes[^1].End < i)
                scopes.RemoveAt(scopes.Count - 1);
            var outer = scopes.Count > 0 ? scopes[^1].Name : fileNamespace;

            if (isNamespace)
            {
                if (!t.Id(i + 1) || t.P(i - 1, '.'))
                    continue;
                var end = i + 1;
                while (t.P(end + 1, '.') && t.Id(end + 2) && _meter.Charge())
                    end += 2;
                var name = Qualify(outer, DottedName(i + 1, end), t.Line(i));
                if (t.P(end + 1, ';'))
                    fileNamespace = name;
                else if (t.P(end + 1, '{'))
                    Enter(end + 1, name);
                continue;
            }

            // "where T : class", "x.record", "typeof(struct)", and the "class" of "record class".
            if (t.P(i - 1, ':') || t.P(i - 1, ',') || t.P(i - 1, '.') || t.P(i - 1, '(') || (keyword != "record" && t.Id(i - 1, "record")))
                continue;
            var nameIndex = i + 1;
            if (keyword == "record" && (t.Id(nameIndex, "class") || t.Id(nameIndex, "struct")))
                nameIndex++;
            if (!t.Id(nameIndex))
                continue;

            var j = nameIndex + 1;
            int angleOpen = -1, angleClose = -1;
            if (t.P(j, '<'))
            {
                angleClose = CloseAngle(j);
                if (angleClose < 0)
                    continue;
                angleOpen = j;
                j = angleClose + 1;
            }
            if (!(t.P(j, '(') || t.P(j, ':') || t.P(j, '{') || t.P(j, ';') || t.Id(j, "where")))
                continue;
            var parametersOpen = -1;
            if (t.P(j, '('))
            {
                if (t.Match[j] < 0)
                    continue;
                parametersOpen = j;
                j = t.Match[j] + 1;
            }

            var bases = new List<CSharpBase>();
            if (t.P(j, ':'))
            {
                j++;
                while (t.Id(j) && !t.Id(j, "where"))
                {
                    if (!_meter.Charge())
                        return false;
                    var first = j;
                    while (t.P(j + 1, '.') && t.Id(j + 2) && _meter.Charge())
                        j += 2;
                    var baseName = t[j].Text;
                    var qualifier = j == first ? "" : DottedName(first, j - 2);
                    var line = t.Line(first);
                    j++;
                    int argsStart = -1, argsEnd = -1, argCount = 0;
                    if (t.P(j, '<'))
                    {
                        var close = CloseAngle(j);
                        if (close < 0)
                            break;
                        argsStart = j + 1;
                        argsEnd = close - 1;
                        argCount = 1;
                        var depth = 0;
                        for (var k = argsStart; k <= argsEnd; k++)
                        {
                            if (t.P(k, '<')) depth++;
                            else if (t.P(k, '>')) depth--;
                            else if (t.P(k, ',') && depth == 0) argCount++;
                        }
                        j = close + 1;
                    }
                    if (t.P(j, '(') && t.Match[j] > j)
                        j = t.Match[j] + 1;
                    // A base whose qualifier is too long to keep is kept under a name that matches nothing.
                    bases.Add(new CSharpBase(qualifier is null ? "" : baseName, qualifier ?? "", argsStart, argsEnd, argCount, line));
                    if (!t.P(j, ','))
                        break;
                    j++;
                }
            }

            // Constraints, up to the body or the ';' of a declaration without one.
            var limit = j + MaxHeaderTokens;
            while (j < t.Count && j < limit && !t.P(j, '{') && !t.P(j, ';') && _meter.Charge())
                j = t.Match[j] > j ? t.Match[j] + 1 : j + 1;
            if (j >= t.Count || j >= limit)
                continue;
            var bodyOpen = t.P(j, '{') && t.Match[j] > j ? j : -1;
            var endIndex = bodyOpen >= 0 ? t.Match[j] : j;

            if (i < unreadUntil)
                continue;
            var qualified = Qualify(outer, t[nameIndex].Text, t.Line(i));
            if (bodyOpen >= 0 && !Enter(bodyOpen, qualified))
                continue;
            if (outer is null || qualified is null)
                continue;

            // Attributes and modifiers: back to the first of them, then the groups in source order.
            var start = i;
            bool isPartial = false, isAbstract = false, isStatic = false;
            while (start > 0)
            {
                if (!_meter.Charge())
                    return false;
                if (t.Id(start - 1) && TypeModifiers.Contains(t[start - 1].Text))
                {
                    isPartial |= t[start - 1].Text == "partial";
                    isAbstract |= t[start - 1].Text == "abstract";
                    isStatic |= t[start - 1].Text == "static";
                    start--;
                }
                else if (t.P(start - 1, ']') && t.Match[start - 1] >= 0)
                    start = t.Match[start - 1];
                else
                    break;
            }
            var type = new CSharpType
            {
                Keyword = keyword, Name = t[nameIndex].Text, Container = outer, KeywordIndex = i, StartIndex = start,
                ParametersOpen = parametersOpen, BodyOpen = bodyOpen, EndIndex = endIndex,
                IsPartial = isPartial, IsAbstract = isAbstract, IsStatic = isStatic,
            };
            for (var k = start; k < i; k++)
            {
                if (!_meter.Charge())
                    return false;
                if (t.P(k, '[') && t.Match[k] > k)
                {
                    ReadAttributeGroup(k, type.Attributes);
                    k = t.Match[k];
                }
            }
            for (var k = angleOpen + 1; angleOpen >= 0 && k < angleClose; k++)
                if (t.Id(k) && (t.P(k + 1, ',') || k + 1 == angleClose))
                    type.TypeParameters.Add(k);
            type.Bases.AddRange(bases);
            Types.Add(type);
        }
        FileNamespace = fileNamespace ?? "";
        return true;

        // Opens the body of a namespace or type. False when it nests too deep: its content is then not read.
        bool Enter(int open, string? name)
        {
            var close = t.Match[open] > open ? t.Match[open] : t.Count;
            if (open < unreadUntil)
                return false;
            if (scopes.Count >= MaxNesting)
            {
                unreadUntil = Math.Max(unreadUntil, close);
                Notes.Add(("type-nesting", t.Line(open)));
                return false;
            }
            scopes.Add((close, name));
            return true;
        }
    }

    /// <summary>The qualified name, or null (with a note) when it is too long to keep or the container was.</summary>
    private string? Qualify(string? outer, string? name, int line)
    {
        if (outer is not null && name is not null && outer.Length + name.Length < MaxNameLength)
            return outer.Length == 0 ? name : outer + "." + name;
        Notes.Add(("name-too-long", line));
        return null;
    }

    private bool ReadUsing(int i)
    {
        if (!_meter.Charge())
            return false;
        var isGlobal = t.Id(i - 1, "global");
        var before = isGlobal ? i - 2 : i - 1;
        if (before >= 0 && !(t.P(before, ';') || t.P(before, '}') || t.P(before, '{')))
            return true;
        var first = i + 1;
        if (!t.Id(first) || t.Id(first, "static"))
            return true;
        var last = first;
        while (t.P(last + 1, '.') && t.Id(last + 2) && _meter.Charge())
            last += 2;
        if (t.P(last + 1, '=') && last == first)
            Aliases.Add(t[first].Text);
        else if (t.P(last + 1, ';') && DottedName(first, last) is { } name)
            (isGlobal ? GlobalUsings : Usings).Add(name);
        return true;
    }

    private bool ReadTypeOf()
    {
        // Each token inside a type body gets the innermost type. Bodies are bracket pairs, so they nest.
        // The header of a type belongs to the type around it.
        var bodies = new List<int>();
        for (var index = 0; index < Types.Count; index++)
            if (Types[index].BodyOpen >= 0)
                bodies.Add(index);
        bodies.Sort((a, b) => Types[a].BodyOpen != Types[b].BodyOpen ? Types[a].BodyOpen.CompareTo(Types[b].BodyOpen) : a.CompareTo(b));
        if (!_meter.Charge(bodies.Count))
            return false;
        var open = new Stack<int>();
        var next = 0;
        for (var i = 0; i < t.Count; i++)
        {
            while (open.Count > 0 && Types[open.Peek()].EndIndex <= i)
                open.Pop();
            _typeOf[i] = open.Count > 0 ? open.Peek() : -1;
            while (next < bodies.Count && Types[bodies[next]].BodyOpen == i)
                open.Push(bodies[next++]);
        }
        return true;
    }

    // ---- scopes -----------------------------------------------------------------------

    private bool ReadScopes()
    {
        var stacks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        // A parameter list of a primary constructor, by its '(' ; a type parameter, by its token: the end of the scope.
        var primary = new Dictionary<int, int>();
        var typeParameters = new Dictionary<int, int>();
        for (var index = 0; index < Types.Count; index++)
        {
            var type = Types[index];
            if (type.ParametersOpen >= 0 && type.BodyOpen >= 0)
                primary[type.ParametersOpen] = index;
            foreach (var token in type.TypeParameters)
                typeParameters[token] = type.EndIndex;
        }

        for (var i = 0; i < t.Count; i++)
        {
            if (!t.Id(i))
                continue;
            if (!_meter.Charge())
                return false;
            var name = t[i].Text;
            CSharpDecl? decl;
            if (typeParameters.TryGetValue(i, out var scopeEnd))
            {
                decl = new CSharpDecl(CSharpDeclKind.TypeParameter, i, scopeEnd, _typeOf[i]);
            }
            else
            {
                if (t.P(i + 1, '<') && !t.P(i - 1, '.') && !t.Id(i - 1, "new"))
                    ReadMethodTypeParameters(i, typeParameters);
                decl = NotTypes.Contains(name) || name == "var" ? null : Classify(i, primary);
            }

            if (decl is not null)
            {
                if (!stacks.TryGetValue(name, out var stack))
                    stacks[name] = stack = [];
                stack.Add(_decls.Count);
                _declOf[i] = _decls.Count;
                _decls.Add(decl);
                continue;
            }
            if (!stacks.TryGetValue(name, out var known))
                continue;
            // Scopes that ended are dropped here, so the innermost live one is on top.
            while (known.Count > 0 && _decls[known[^1]].End < i)
                known.RemoveAt(known.Count - 1);
            if (known.Count == 0)
                continue;
            var visible = _decls[known[^1]];
            if (visible.Kind != CSharpDeclKind.TypeParameter && visible.TypeIndex != _typeOf[i])
                continue;
            _declOf[i] = known[^1];
            if (IsAssignment(i) && !t.P(i - 1, '.'))
                (visible.Assignments ??= []).Add(i);
        }
        return true;
    }

    private bool IsAssignment(int i) => t.P(i + 1, '=') && !t.P(i + 2, '=') && !t.P(i + 2, '>');

    private CSharpDecl? Classify(int i, Dictionary<int, int> primary)
    {
        // "x => …". Also the name of an expression-bodied member, which is then a name of unknown type up to its ';'.
        if (t.P(i + 1, '=') && t.P(i + 2, '>') && !t.P(i - 1, '.'))
            return new CSharpDecl(CSharpDeclKind.LambdaParameter, i, ArrowEnd(i + 1), _typeOf[i]);

        var parent = t.Parent[i];
        var typed = HasTypeBefore(i);
        if (parent >= 0 && t.P(parent, '(') && (t.P(i + 1, ',') || t.P(i + 1, ')') || IsAssignment(i)))
        {
            var close = t.Match[parent];
            if (close < 0)
                return typed ? new CSharpDecl(CSharpDeclKind.LambdaParameter, i, t.Count, _typeOf[i]) : null;
            // "var (a, b) = …": names of a type that the text does not give.
            if (t.Id(parent - 1, "var"))
                return new CSharpDecl(CSharpDeclKind.Variable, i, BlockEnd(i), _typeOf[i]);
            if (primary.TryGetValue(parent, out var owner))
                return typed ? Typed(new CSharpDecl(CSharpDeclKind.Variable, i, Types[owner].EndIndex, owner), i) : null;
            if (t.Id(parent - 1) && ControlKeywords.Contains(t[parent - 1].Text))
                return typed ? Local(i) : null;
            var end = BodyEnd(close);
            if (end < 0)
            {
                if (!typed)
                    return null;
                // "out T x" and "is T x" declare a variable of the enclosing block. A parameter of a
                // declaration without a body (abstract, interface, delegate) is in scope for its own list only.
                return IsOutOrPattern(i) ? Local(i) : Typed(new CSharpDecl(CSharpDeclKind.Variable, i, close, _typeOf[i]), i);
            }
            if (typed)
                return Typed(new CSharpDecl(CSharpDeclKind.Variable, i, end, _typeOf[i]), i);
            // Without a type, only "(a, b) => …" declares names.
            return t.P(close + 1, '=') && t.P(close + 2, '>') && (t.P(i - 1, ',') || t.P(i - 1, '('))
                ? new CSharpDecl(CSharpDeclKind.LambdaParameter, i, end, _typeOf[i])
                : null;
        }
        if (typed && (t.P(i + 1, ';') || IsAssignment(i) || t.Id(i + 1, "in")))
            return Local(i);
        // Two names in a row are a declaration whatever follows: a pattern variable, a property, a
        // declarator before a ','. "Type Name(" and "Type Name<" declare a method, and "Type A.B" an explicit implementation.
        var before = t.P(i - 1, '?') ? i - 2 : i - 1;
        return t.Id(before) && !NotTypes.Contains(t[before].Text) && !(t.P(i + 1, '(') || t.P(i + 1, '<') || t.P(i + 1, '.')) ? Local(i) : null;
    }

    private bool IsOutOrPattern(int i)
    {
        var k = t.P(i - 1, '?') ? i - 2 : i - 1;
        if (!t.Id(k))
            return false;
        for (var steps = 0; steps < 8 && t.P(k - 1, '.') && t.Id(k - 2); steps++)
            k -= 2;
        return t.Id(k - 1, "out") || t.Id(k - 1, "is") || t.Id(k - 1, "case") || t.Id(k - 1, "not") || t.Id(k - 1, "and") || t.Id(k - 1, "or");
    }

    private int BlockEnd(int i)
    {
        var brace = _enclosingBrace[i];
        return brace >= 0 && t.Match[brace] > brace ? t.Match[brace] : t.Count;
    }

    private CSharpDecl Local(int i)
    {
        var decl = Typed(new CSharpDecl(CSharpDeclKind.Variable, i, BlockEnd(i), _typeOf[i]), i);
        if (IsAssignment(i))
            decl.Assignments = [i];
        return decl;
    }

    private bool HasTypeBefore(int i)
    {
        var k = i - 1;
        if (t.P(k, '?'))
            k--;
        return t.P(k, '>') || t.P(k, ']') || (t.Id(k) && !NotTypes.Contains(t[k].Text));
    }

    private CSharpDecl Typed(CSharpDecl decl, int i)
    {
        var k = i - 1;
        if (t.P(k, '?'))
            k--;
        if (t.P(k, ']'))
            decl.IsArray = true;
        else if (t.Id(k, "var"))
            decl.IsVar = true;
        else if (t.Id(k))
            decl.TypeName = t[k].Text;
        else if (t.P(k, '>') && AngleOpen(k) is > 0 and var open && t.Id(open - 1))
        {
            decl.TypeName = t[open - 1].Text;
            if (DottedName(open + 1, k - 1) is { } argument)
                decl.TypeArgument = argument[(argument.LastIndexOf('.') + 1)..];
        }
        return decl;
    }

    /// <summary>"Name&lt;T, U&gt;(…)" followed by a body declares T and U for the parameters and the body.</summary>
    private void ReadMethodTypeParameters(int i, Dictionary<int, int> typeParameters)
    {
        var close = CloseAngle(i + 1);
        if (close < 0 || !t.P(close + 1, '(') || t.Match[close + 1] < 0)
            return;
        var end = BodyEnd(t.Match[close + 1]);
        if (end < 0)
            return;
        for (var k = i + 2; k < close; k++)
            if (t.Id(k) && (t.P(k + 1, ',') || k + 1 == close) && (t.P(k - 1, ',') || k == i + 2))
                typeParameters[k] = end;
    }

    /// <summary>The last token of the body that follows the ')' of a parameter list, or -1 when no body follows.</summary>
    private int BodyEnd(int close)
    {
        var a = close + 1;
        for (var steps = 0; a < t.Count && steps < MaxHeaderTokens; steps++)
        {
            if (!_meter.Charge())
                return -1;
            if (t.P(a, '{'))
                return t.Match[a] > a ? t.Match[a] : t.Count;
            if (t.P(a, '=') && t.P(a + 1, '>'))
                return ArrowEnd(a);
            // A constraint clause or a constructor initializer can stand between: "where T : class", ": base(x)".
            if (steps == 0 && !(t.Id(a, "where") || t.P(a, ':')))
                return -1;
            if (t.P(a, ';') || t.P(a, ')') || t.P(a, '}') || t.P(a, ']') || t.P(a, '='))
                return -1;
            a = t.Match[a] > a ? t.Match[a] + 1 : a + 1;
        }
        return -1;
    }

    /// <summary>The last token of what follows the "=&gt;" at <paramref name="arrow"/>.</summary>
    private int ArrowEnd(int arrow)
    {
        if (t.P(arrow + 2, '{') && t.Match[arrow + 2] > arrow)
            return t.Match[arrow + 2];
        var semicolon = _nextSemicolon[arrow];
        if (semicolon >= 0)
            return semicolon;
        var parent = t.Parent[arrow];
        return parent >= 0 && t.Match[parent] > parent ? t.Match[parent] : t.Count;
    }
}
