using System.Text;
using System.Text.RegularExpressions;

namespace Vellum.Modules.Ai;

/// <summary>
/// Replaces text that looks like a credential before a prompt leaves the process. It is
/// always on.
/// <para>
/// <b>Redaction is best-effort.</b> It finds credentials by their shape and by the name
/// beside them, so it misses a credential that has neither: a short password in prose, a
/// value under a name that says nothing, a secret in a format it does not know. It is a
/// second line of defence against a credential that was committed by mistake. It is NOT the
/// control that protects private code. That control is the gate in <see cref="AiClient"/>:
/// a part of class <see cref="AiDataClass.PrivateRepository"/> is sent only when the
/// workspace's effective <c>privacy.sendPrivateCodeToAi</c> is true.
/// </para>
/// <para>
/// It errs toward removing too much: a field named <c>token</c> loses its value whether or
/// not the value was secret. What it removes:
/// </para>
/// <list type="bullet">
/// <item>PEM private-key blocks; a block with no END line to the end of the text; and a key
/// body whose BEGIN line is missing (lines of base64 that end at an END line).</item>
/// <item>Slack and Discord webhook URLs, and the user information of any URL
/// (<c>scheme://user:password@</c>, <c>redis://:password@</c>).</item>
/// <item>The value of <c>name = value</c>, <c>name: value</c>, <c>name := value</c> and
/// <c>"name": "value"</c> when the name holds a credential word, to the closing quote or to
/// the end of the line, so a value of several words goes as a whole. The words, found
/// anywhere in the name after <c>_ - .</c> are taken out: secret, token, password, passwd,
/// passphrase, credential, api key, access key, private key, encryption key, account key,
/// signature, connection string, authorization. The short words must be a whole part of the
/// name (<c>DB_PASS</c>, <c>dbPass</c>, <c>basic_auth</c>): pass, pwd, sig, auth, cookie.
/// This covers HTTP headers (<c>Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c>,
/// <c>x-api-key</c>), Azure <c>AccountKey=</c> and <c>SharedAccessSignature=</c>, and a
/// <c>sig=</c> query value. A name that counts tokens (<c>max_tokens</c>) is not a
/// credential name, and a value that is a literal (<c>true</c>, <c>null</c>) or a type name
/// stays.</item>
/// <item>A value of <see cref="MinRandomLength"/> or more characters under any other name
/// when it looks random: letters of both cases and a digit, and a Shannon entropy of at
/// least <see cref="MinEntropyBitsPerChar"/> bits per character. A hexadecimal value (a git
/// SHA, a SHA-256, a UUID) has at most 4 bits per character and is never removed by this
/// rule, because provenance needs it readable.</item>
/// <item><c>Bearer &lt;token&gt;</c> anywhere, JSON Web Tokens, and the token shapes of
/// GitHub, GitLab, OpenAI, Anthropic, AWS, Google, Slack, npm, Hugging Face, SendGrid and
/// Stripe.</item>
/// </list>
/// <para>
/// Every regular expression runs without backtracking, and the scan for assignments looks
/// back and ahead a bounded distance from each separator, so the time is linear in the
/// length of the text and repository text cannot make it slow.
/// </para>
/// </summary>
public static partial class SecretRedactor
{
    public const string Marker = "[REDACTED]";

    /// <summary>The shortest value that the entropy rule looks at.</summary>
    public const int MinRandomLength = 32;

    /// <summary>
    /// The entropy rule's threshold, in bits per character. Lower-case hexadecimal text has at
    /// most 4.0; random base64 or base62 text of 32 characters has about 4.5 or more.
    /// </summary>
    public const double MinEntropyBitsPerChar = 4.1;

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private const int MaxKeyChars = 128;
    private const int MaxGapChars = 8;
    private const int MaxProbeChars = 512;

    private static readonly string[] Words =
    [
        "secret", "password", "passwd", "passphrase", "credential", "apikey", "accesskey", "privatekey",
        "encryptionkey", "accountkey", "signature", "connectionstring", "authorization",
    ];

    private static readonly string[] WholeWords = ["pass", "pwd", "sig", "auth", "cookie"];

    private static readonly string[] Literals =
    [
        "true", "false", "null", "nil", "none", "undefined", "yes", "no",
        "string", "str", "number", "int", "bool", "boolean", "bytes", "any", "object",
    ];

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = PemBlock().Replace(text, Marker);
        text = PemWithoutEnd().Replace(text, Marker);
        text = PemWithoutBegin().Replace(text, Marker);
        text = WebhookUrl().Replace(text, $"$1{Marker}");
        text = UrlUserInfo().Replace(text, $"$1{Marker}@");
        text = Assignments(text);
        text = Bearer().Replace(text, match =>
        {
            var token = match.Groups[2].ValueSpan;
            // "bearer" is also a word of prose. A token has a digit or is long.
            return token.Length >= 20 || token.IndexOfAnyInRange('0', '9') >= 0 ? match.Groups[1].Value + Marker : match.Value;
        });
        text = Jwt().Replace(text, Marker);
        return KnownToken().Replace(text, Marker);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z0-9 ]*-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY[A-Z0-9 ]*-----", Options)]
    private static partial Regex PemBlock();

    // The end line is missing: the rest of the text can be key material.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY[A-Z0-9 ]*-----[\s\S]*", Options)]
    private static partial Regex PemWithoutEnd();

    // The begin line is missing: the lines of base64 that end at the end line are key material.
    [GeneratedRegex(@"(?:[A-Za-z0-9+/=]{16,}[ \t]*\r?\n[ \t]*)*[A-Za-z0-9+/=]{16,}\s*-----END [A-Z0-9 ]*PRIVATE KEY[A-Z0-9 ]*-----", Options)]
    private static partial Regex PemWithoutBegin();

    // A Slack or Discord webhook URL is a credential: whoever has it can post. The host stays readable.
    [GeneratedRegex(@"(https://(?:hooks\.slack\.com/(?:services|workflows|triggers)/|(?:[a-z]+\.)?discord(?:app)?\.com/api/(?:v[0-9]+/)?webhooks/))[A-Za-z0-9/._-]+", Options)]
    private static partial Regex WebhookUrl();

    // scheme://user:password@host and scheme://:password@host. The part before "@" holds no "/", "?" or "#".
    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*://)[^/\s@""'<>?#]*@", Options)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"(\b[Bb]earer[ \t]+)([A-Za-z0-9._~+/=-]{8,})", Options)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", Options)]
    private static partial Regex Jwt();

    // GitHub, OpenAI and Anthropic, Stripe, GitLab, Slack, AWS, Google, npm, Hugging Face and SendGrid token shapes.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,}|[spr]k_(?:live|test)_[A-Za-z0-9]{16,}|glpat-[A-Za-z0-9_-]{16,}|xox[abprs]-[A-Za-z0-9-]{10,}|A[KS]IA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|ya29\.[A-Za-z0-9._-]{20,}|npm_[A-Za-z0-9]{36}|hf_[A-Za-z0-9]{30,}|SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,})", Options)]
    private static partial Regex KnownToken();

    /// <summary>
    /// One pass over the text. At each <c>=</c>, <c>:</c> or <c>:=</c> it reads the name
    /// before it and the value after it. A credential name loses its value; any other name
    /// loses a value that looks random.
    /// </summary>
    private static string Assignments(string text)
    {
        StringBuilder? result = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is not ('=' or ':'))
            {
                i++;
                continue;
            }

            var separatorEnd = i + 1;
            var next = separatorEnd < text.Length ? text[separatorEnd] : '\0';
            var previous = i > 0 ? text[i - 1] : '\0';
            if (c == ':')
            {
                if (next == '=')
                {
                    separatorEnd++;
                }
                else if (next == ':' || previous == ':')
                {
                    // "::" is a scope, not an assignment.
                    i++;
                    continue;
                }
            }
            else if (next is '=' or '>')
            {
                // "==" and "=>".
                i += 2;
                continue;
            }
            else if (previous is '!' or '<' or '>' or '=')
            {
                i++;
                continue;
            }

            // The name: back over a little white space and one closing quote, then over name characters.
            var k = i - 1;
            while (k >= 0 && i - k <= MaxGapChars && text[k] is ' ' or '\t')
                k--;
            if (k >= 0 && text[k] is '"' or '\'' or '`')
                k--;
            var keyEnd = k + 1;
            while (k >= 0 && keyEnd - k <= MaxKeyChars && IsNameChar(text[k]))
                k--;
            var key = text.AsSpan(k + 1, keyEnd - (k + 1));

            var valueStart = separatorEnd;
            while (valueStart < text.Length && text[valueStart] is ' ' or '\t')
                valueStart++;
            if (key.Length == 0 || valueStart >= text.Length || text[valueStart] is '\n' or '\r')
            {
                i = separatorEnd;
                continue;
            }

            int from, to, resume;
            if (IsCredentialName(key))
            {
                (from, to, resume) = CredentialValue(text, valueStart);
                if (to == from || IsLiteral(text.AsSpan(from, to - from)))
                {
                    i = separatorEnd;
                    continue;
                }
            }
            else if (!TryRandomValue(text, valueStart, out from, out to, out resume))
            {
                i = separatorEnd;
                continue;
            }

            result ??= new StringBuilder(text.Length);
            result.Append(text, copied, from - copied).Append(Marker);
            copied = to;
            i = resume;
        }

        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The value of a credential name: the text inside its quotes, or the rest of the line, or a YAML block.</summary>
    private static (int From, int To, int Resume) CredentialValue(string text, int start)
    {
        var quote = text[start];
        if (quote is '"' or '\'' or '`')
        {
            var j = start + 1;
            while (j < text.Length && text[j] != quote && text[j] is not ('\n' or '\r'))
                j += text[j] == '\\' && j + 1 < text.Length && text[j + 1] is not ('\n' or '\r') ? 2 : 1;
            j = Math.Min(j, text.Length);
            // With a closing quote the scan goes on behind it: one line can hold more pairs.
            return j < text.Length && text[j] == quote ? (start + 1, j, j + 1) : (start + 1, j, j);
        }

        var end = LineEnd(text, start);
        var valueEnd = end;
        while (valueEnd > start && text[valueEnd - 1] is ' ' or '\t')
            valueEnd--;

        // "name: |" and "name: >" start a YAML block: the indented lines below are the value.
        if (text[start] is '|' or '>' && valueEnd - start <= 3)
        {
            var block = end;
            while (block < text.Length)
            {
                var line = block;
                while (line < text.Length && text[line] is '\n' or '\r')
                    line++;
                if (line >= text.Length || text[line] is not (' ' or '\t'))
                    break;
                block = LineEnd(text, line);
            }

            if (block > end)
                return (start, block, block);
        }

        return (start, valueEnd, end);
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.AsSpan(from).IndexOfAny('\n', '\r');
        return end < 0 ? text.Length : from + end;
    }

    /// <summary>A value under a name that is not a credential name, when the whole value is one long random-looking run.</summary>
    private static bool TryRandomValue(string text, int start, out int from, out int to, out int resume)
    {
        from = to = resume = 0;
        var quote = text[start] is '"' or '\'' or '`' ? text[start] : '\0';
        var runStart = quote == '\0' ? start : start + 1;

        var probeEnd = runStart;
        var probeLimit = Math.Min(text.Length, runStart + MaxProbeChars);
        while (probeEnd < probeLimit && IsRunChar(text[probeEnd]))
            probeEnd++;
        if (probeEnd - runStart < MinRandomLength)
            return false;

        var end = probeEnd;
        var padding = 0;
        if (end == probeLimit && end < text.Length && IsRunChar(text[end]))
        {
            // Longer than the probe. Only a run whose start looks random is read to its end, so the cost stays bounded.
            if (!LooksRandom(text.AsSpan(runStart, probeEnd - runStart), padded: true))
                return false;
            while (end < text.Length && IsRunChar(text[end]))
                end++;
        }

        while (end < text.Length && padding < 2 && text[end] == '=')
        {
            end++;
            padding++;
        }

        // The run must be the whole value.
        var after = end < text.Length ? text[end] : '\n';
        var whole = quote != '\0' ? after == quote : char.IsWhiteSpace(after) || after is ',' or ';' or ')' or '}' or ']' or '&' or '"' or '\'';
        if (!whole || !LooksRandom(text.AsSpan(runStart, Math.Min(end - padding, runStart + MaxProbeChars) - runStart), padding > 0))
            return false;

        (from, to, resume) = (runStart, end, end);
        return true;
    }

    private static bool LooksRandom(ReadOnlySpan<char> run, bool padded)
    {
        bool digit = false, upper = false, lower = false, slash = false, plus = false, hexOnly = true;
        Span<int> counts = stackalloc int[128];
        counts.Clear();
        foreach (var c in run)
        {
            counts[c]++;
            digit |= char.IsAsciiDigit(c);
            upper |= char.IsAsciiLetterUpper(c);
            lower |= char.IsAsciiLetterLower(c);
            slash |= c == '/';
            plus |= c == '+';
            hexOnly &= char.IsAsciiHexDigit(c) || c == '-';
        }

        // A SHA or a UUID. A path has slashes and no base64 mark. A word has no digit or one case.
        if (hexOnly || !digit || !upper || !lower || (slash && !plus && !padded))
            return false;

        var entropy = 0.0;
        foreach (var count in counts)
        {
            if (count == 0)
                continue;
            var share = (double)count / run.Length;
            entropy -= share * Math.Log2(share);
        }

        return entropy >= MinEntropyBitsPerChar;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    private static bool IsRunChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '_' or '-';

    private static bool IsLiteral(ReadOnlySpan<char> value)
    {
        value = value.TrimEnd(",;)}]? \t");
        foreach (var literal in Literals)
        {
            if (value.Equals(literal, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsCredentialName(ReadOnlySpan<char> name)
    {
        // The name in lower case without its separators: "SECRET_KEY_BASE" reads "secretkeybase".
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (c is not ('_' or '-' or '.'))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        ReadOnlySpan<char> joined = buffer[..length];
        foreach (var word in Words)
        {
            if (joined.IndexOf(word, StringComparison.Ordinal) >= 0)
                return true;
        }

        // "token", and not "tokens": a name that ends in "tokens" counts them.
        for (var at = 0; at < joined.Length;)
        {
            var found = joined[at..].IndexOf("token", StringComparison.Ordinal);
            if (found < 0)
                break;
            at += found + 5;
            if (at >= joined.Length || joined[at] != 's')
                return true;
        }

        // The short words are a whole part of the name: between separators or at a change of case.
        var partStart = 0;
        for (var j = 1; j <= name.Length; j++)
        {
            var boundary = j == name.Length
                || name[j] is '_' or '-' or '.'
                || (char.IsAsciiLetterUpper(name[j]) && (char.IsAsciiLetterLower(name[j - 1]) || char.IsAsciiDigit(name[j - 1])))
                || (char.IsAsciiLetterUpper(name[j]) && j + 1 < name.Length && char.IsAsciiLetterLower(name[j + 1]) && char.IsAsciiLetterUpper(name[j - 1]));
            if (!boundary)
                continue;

            var part = name[partStart..j];
            foreach (var word in WholeWords)
            {
                if (part.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            partStart = j < name.Length && name[j] is '_' or '-' or '.' ? j + 1 : j;
        }

        return false;
    }
}
