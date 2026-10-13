using System.Text.RegularExpressions;

namespace Vellum.Modules.Ai;

/// <summary>
/// Replaces text that looks like a credential before a prompt leaves the process. It is
/// always on. It errs toward removing too much: a field named <c>token</c> loses its value
/// whether or not the value was secret. Every pattern runs without backtracking, so
/// repository text cannot make it slow.
/// </summary>
public static partial class SecretRedactor
{
    public const string Marker = "[REDACTED]";
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = PrivateKey().Replace(text, Marker);
        text = Jwt().Replace(text, Marker);
        text = KnownToken().Replace(text, Marker);
        text = AuthorizationHeader().Replace(text, $"$1{Marker}");
        text = UrlCredentials().Replace(text, $"$1{Marker}@");
        return Assignment().Replace(text, $"$1$2{Marker}");
    }

    // A PEM private key, or the rest of the text when the end line is missing.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----(?:[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----|[\s\S]*)", Options)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", Options)]
    private static partial Regex Jwt();

    // GitHub, OpenAI and Anthropic, AWS, Google, Slack, GitLab and Stripe token shapes.
    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|A[KS]IA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|xox[abprs]-[A-Za-z0-9-]{10,}|glpat-[A-Za-z0-9_-]{20,}|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}", Options)]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"((?:authorization|x-api-key|x-goog-api-key)""?\s*[:=]\s*""?(?:(?:bearer|basic|token)\s+)?)[^\s""',;]+", Options | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    // scheme://user:password@host, as in a database or broker connection string.
    [GeneratedRegex(@"([a-z][a-z0-9+.-]*://)[^/\s:@""']+:[^/\s@""']+@", Options | RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    // name = value, name: value and "name": "value", where the name ends in a credential word.
    [GeneratedRegex(@"([A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|credentials?|connection[_-]?string))(""?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;""']+)", Options | RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();
}
