using System.Text;
using System.Text.RegularExpressions;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Modules.Import.Scans;

/// <summary>
/// The check that content passes before it is stored, and before <c>vellum scan</c> gives a
/// file to the extractors (it compiles this same file). It runs on the bytes GitHub returned,
/// at the moment they arrive: content that fails is never written anywhere, and only the
/// reason is kept. So no later step, and no failed scan, can serve it.
/// <para>
/// The check depends on the content alone and not on the path, because a blob is stored
/// once for every path that holds it.
/// </para>
/// </summary>
internal static partial class ContentClearance
{
    /// <summary>A file is withheld when at least this many of its lines look like credentials and they are at least <see cref="DenseShare"/> of its lines with text.</summary>
    public const int DenseLines = 5;
    public const double DenseShare = 0.2;
    /// <summary>The most characters of one line that the check reads. A longer line costs more to check than it can tell.</summary>
    public const int MaxCheckedLineLength = 2000;

    [GeneratedRegex(@"^\s*-?\s*[""']?kind[""']?\s*[:=]\s*[""']?Secret[""']?\s*,?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKind();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    /// <summary>True when the bytes are the git blob that <paramref name="blobSha"/> names: SHA-1 over <c>blob &lt;length&gt;\0</c> and the bytes.</summary>
    public static bool IsBlob(byte[] content, string blobSha) =>
        string.Equals(Sources.GitBlob.Sha(content), blobSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null when the content may be stored. Else why not: <c>binary-content</c> for bytes
    /// that are not text, <c>secret-file</c> for a manifest that declares a Secret or text
    /// that holds a private key, <c>credential-dense</c> for text with many credential-like lines.
    /// </summary>
    public static string? Withhold(byte[] content)
    {
        if (Array.IndexOf(content, (byte)0) >= 0)
            return "binary-content";

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return "binary-content";
        }

        if (PrivateKey().IsMatch(text))
            return "secret-file";

        int withText = 0, secret = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsWhiteSpace())
                continue;
            withText++;
            var checkedLine = line.Length > MaxCheckedLineLength ? line[..MaxCheckedLineLength] : line;
            var value = checkedLine.ToString();
            if (SecretKind().IsMatch(value))
                return "secret-file";
            if (CredentialGuard.LooksSecret(value))
                secret++;
        }
        return secret >= DenseLines && secret >= withText * DenseShare ? CoverageGap.CredentialDense : null;
    }
}
