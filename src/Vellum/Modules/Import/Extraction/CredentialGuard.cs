using System.Text.RegularExpressions;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Decides whether a value that an extractor read looks like a credential. Every key, name
/// and attribute passes through here before it enters a fact, so an extractor that reads a
/// value it should not have still cannot publish it.
/// <para>
/// This is the second line. The first is structural: an extractor does not read a VALUE at
/// all where a secret can stand (command arguments, environment values, schema defaults and
/// examples, URL user information). A short secret under a key with an ordinary name has no
/// shape, and only the first line stops it.
/// </para>
/// </summary>
internal static partial class CredentialGuard
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    // Words that name a credential, in a key of any casing and with any prefix or suffix:
    // "DB_PASSWORD", "myApiKey", "client-secret-value".
    private const string Words =
        "password|passwd|passphrase|pwd|secret|token|api[_\\-]?key|access[_\\-]?key|account[_\\-]?key|sharedaccesskey"
        + "|private[_\\-]?key|credential|authorization|requirepass";

    // Token formats that are recognisable by shape.
    [GeneratedRegex(
        @"(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}"
        + @"|sk-[A-Za-z0-9_\-]{20,}|sk_(live|test)_[A-Za-z0-9]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}"
        + @"|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}"
        + @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}|-----BEGIN",
        Options)]
    private static partial Regex KnownToken();

    // A credential-named key with a value: "Password=x", "--token=x", "api_key: x",
    // "DB_PASSWORD=x", "\"password\":\"x\"", "Authorization: Bearer x". The key can be quoted.
    // A key followed by '{' or '[' is a name in a document (a schema property), not a value.
    [GeneratedRegex(
        "(" + Words + ")[A-Za-z0-9_\\-]*[\"']?\\s*[=:]\\s*[\"']?[^\\s;,&\"'{}\\[\\]]",
        Options | RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    // A flag with a value after a space: "--password X", "--api-key X", "--requirepass X", "-p X".
    // A flag that is followed by another flag has no value.
    [GeneratedRegex(
        "(^|[\\s\"'\\[,])(--?[A-Za-z0-9_\\-]*(pass|pwd|secret|token|key|auth|credential)[A-Za-z0-9_\\-]*|-p)[\"']?[\\s,]+[\"']?[^\\s\\-\"',]",
        Options | RegexOptions.IgnoreCase)]
    private static partial Regex FlagWithValue();

    [GeneratedRegex(@"(bearer|basic)\s+[A-Za-z0-9+/=_\-\.]{12,}", Options | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"sha(256|512):[0-9a-f]{64,128}", Options)]
    private static partial Regex Digest();

    [GeneratedRegex("(" + Words + ")", Options | RegexOptions.IgnoreCase)]
    private static partial Regex CredentialWord();

    /// <summary>True for text that holds a credential in any of the shapes this guard knows.</summary>
    public static bool LooksSecret(string value) =>
        value.Length > 0
        && (Assignment().IsMatch(value) || FlagWithValue().IsMatch(value) || HasUserInfo(value) || LooksSecretToken(value));

    /// <summary>
    /// True for a value that has the shape of a token itself. This is the whole check for a
    /// declared name: <c>Token:string</c> names a field and is not a secret.
    /// </summary>
    public static bool LooksSecretToken(string value) =>
        value.Length >= 8 && (KnownToken().IsMatch(value) || AuthorizationHeader().IsMatch(value) || HasOpaqueWord(value));

    /// <summary>True when a property, variable or flag name says that its value is a credential.</summary>
    public static bool IsCredentialName(string name) => CredentialWord().IsMatch(name);

    /// <summary>Letters, digits, '_', '-' and '.', at most 40 characters: a value that is an identifier and not free text.</summary>
    public static bool IsIdentifierToken(string value)
    {
        if (value.Length is 0 or > 40)
            return false;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'))
                return false;
        return true;
    }

    /// <summary>
    /// "user:password@" in a word, with or without a scheme: <c>amqp://u:p@host</c>,
    /// <c>//u:p@host</c>, <c>u:p@host</c>. An image digest (<c>name:tag@sha256:…</c>) is not one.
    /// </summary>
    private static bool HasUserInfo(string value)
    {
        for (var at = value.IndexOf('@'); at >= 0; at = value.IndexOf('@', at + 1))
        {
            var after = value.AsSpan(at + 1);
            if (after.StartsWith("sha256:", StringComparison.Ordinal) || after.StartsWith("sha512:", StringComparison.Ordinal))
                continue;
            // The word before the '@', without a scheme and without the path of an image reference.
            var start = at;
            while (start > 0 && !char.IsWhiteSpace(value[start - 1]) && value[start - 1] is not ('"' or '\'' or ',' or ';' or '=' or '(' or '<'))
                start--;
            var word = value.AsSpan(start, at - start);
            var scheme = word.IndexOf("//", StringComparison.Ordinal);
            if (scheme >= 0)
                word = word[(scheme + 2)..];
            if (word.Contains(':'))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A server URL as <c>scheme://host[:port]/path</c>: no user information, no query, no
    /// fragment. Null when the text cannot be parsed safely; the caller then emits nothing.
    /// <list type="bullet">
    /// <item>The scheme is optional: <c>https://host/x</c>, <c>//host/x</c> and <c>host/x</c> are read.</item>
    /// <item>User information ends at the LAST '@' of the authority.</item>
    /// <item>An '@' after the first '/' is ambiguous (a '/' in a password, or an '@' in the
    /// path): the URL is refused.</item>
    /// <item>The host is letters, digits, '.', '-', '_' and <c>{variable}</c> parts, or a
    /// bracketed IPv6 address; the port is digits or one <c>{variable}</c>.</item>
    /// <item>A relative reference (<c>/v1</c>) has no authority and keeps its path.</item>
    /// </list>
    /// </summary>
    public static string? SafeUrl(string url)
    {
        url = url.Trim();
        if (url.Length is 0 or > 2000)
            return null;
        foreach (var c in url)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '"' or '\'' or '<' or '>')
                return null;

        var scheme = "";
        var rest = url;
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator > 0 && IsScheme(url.AsSpan(0, separator)))
        {
            scheme = url[..(separator + 3)];
            rest = url[(separator + 3)..];
        }
        else if (url.StartsWith("//", StringComparison.Ordinal))
        {
            scheme = "//";
            rest = url[2..];
        }

        var end = rest.IndexOfAny(['?', '#']);
        var slash = rest.IndexOf('/');
        var lastAt = rest.LastIndexOf('@');
        if (lastAt >= 0)
        {
            // Every '@' must stand in the authority, and the authority must end before a query.
            if ((slash >= 0 && lastAt > slash) || (end >= 0 && end < lastAt && (slash < 0 || end < slash)))
                return null;
        }
        else if (scheme.Length == 0)
        {
            // No scheme and no user information: a relative reference. Only its path is kept.
            var path = end < 0 ? rest : rest[..end];
            return path.Length == 0 ? null : path;
        }
        if (end >= 0)
            rest = rest[..end];
        slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        authority = authority[(authority.LastIndexOf('@') + 1)..];

        string host, port = "";
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                return null;
            host = authority[..(close + 1)];
            if (close + 1 < authority.Length)
            {
                if (authority[close + 1] != ':')
                    return null;
                port = authority[(close + 2)..];
            }
            foreach (var c in host.AsSpan(1, host.Length - 2))
                if (!(char.IsAsciiHexDigit(c) || c is ':' or '.'))
                    return null;
        }
        else
        {
            // A ':' inside "{…}" belongs to the variable; the port follows the last ':' outside one.
            var colon = -1;
            var depth = 0;
            for (var i = 0; i < authority.Length; i++)
            {
                if (authority[i] == '{') depth++;
                else if (authority[i] == '}') depth--;
                else if (authority[i] == ':' && depth == 0) colon = i;
            }
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? "" : authority[(colon + 1)..];
            foreach (var c in host)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '{' or '}'))
                    return null;
        }
        if (host.Length == 0)
            return null;
        if (port.Length > 0 && !(port.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0 || (port[0] == '{' && port[^1] == '}' && port.IndexOf('}') == port.Length - 1)))
            return null;

        return scheme + host + (port.Length > 0 ? ":" + port : "") + (slash < 0 ? "" : rest[slash..]);
    }

    private static bool IsScheme(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !char.IsAsciiLetter(text[0]))
            return false;
        foreach (var c in text)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '.' or '-'))
                return false;
        return true;
    }

    /// <summary>
    /// A long run of letters and digits with no separators is treated as an opaque secret.
    /// An image digest is the one such run that is public by design.
    /// </summary>
    private static bool HasOpaqueWord(string value)
    {
        if (value.Length < 32)
            return false;
        if (value.Contains("sha", StringComparison.Ordinal))
            value = Digest().Replace(value, "");

        int run = 0, digits = 0, letters = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : ' ';
            var isDigit = c is >= '0' and <= '9';
            var isLetter = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            if (isDigit || isLetter || c is '_' or '+' or '=')
            {
                run++;
                if (isDigit) digits++;
                if (isLetter) letters++;
                continue;
            }
            if (run >= 32 && digits >= 3 && letters >= 3)
                return true;
            run = digits = letters = 0;
        }
        return false;
    }
}
