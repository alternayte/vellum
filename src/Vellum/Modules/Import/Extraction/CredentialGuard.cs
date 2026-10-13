using System.Text.RegularExpressions;

namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// Decides whether a value that an extractor read looks like a credential. Every key, name
/// and attribute passes through here before it enters a fact, so an extractor that reads a
/// value it should not have still cannot publish it.
/// </summary>
internal static partial class CredentialGuard
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    // Token formats that are recognisable by shape.
    [GeneratedRegex(
        @"(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}"
        + @"|sk-[A-Za-z0-9_\-]{20,}|sk_(live|test)_[A-Za-z0-9]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}"
        + @"|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}"
        + @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}|-----BEGIN",
        Options)]
    private static partial Regex KnownToken();

    // "Password=x", "--token=x", "api_key: x", "Authorization: Bearer x".
    [GeneratedRegex(
        @"(password|passwd|pwd|secret|token|api[_\-]?key|access[_\-]?key|accountkey|sharedaccesskey|client[_\-]?secret|authorization)\s*[=:]\s*[^\s;,&""'{}\[\]]",
        Options | RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(bearer|basic)\s+[A-Za-z0-9+/=_\-\.]{12,}", Options | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    // "scheme://user:password@host"
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^/\s@]*:[^/\s@]*@", Options)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"sha(256|512):[0-9a-f]{64,128}", Options)]
    private static partial Regex Digest();

    public static bool LooksSecret(string value)
    {
        if (value.Length < 8)
            return false;
        return KnownToken().IsMatch(value)
            || Assignment().IsMatch(value)
            || AuthorizationHeader().IsMatch(value)
            || UrlUserInfo().IsMatch(value)
            || HasOpaqueWord(value);
    }

    /// <summary>
    /// A URL without user information, query or fragment, or null when the text is not an
    /// absolute URL. Tokens in URLs live in those three parts.
    /// </summary>
    public static string? ScrubUrl(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0)
            return null;
        var rest = url[(scheme + 3)..];
        var end = rest.IndexOfAny(['?', '#']);
        if (end >= 0)
            rest = rest[..end];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
            authority = authority[(at + 1)..];
        return url[..(scheme + 3)] + authority + (slash < 0 ? "" : rest[slash..]);
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
