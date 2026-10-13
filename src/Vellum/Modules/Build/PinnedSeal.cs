using System.Security.Cryptography;
using System.Text;
using Vellum.Modules.Settings.Crypto;

namespace Vellum.Modules.Build;

/// <summary>
/// The mark that says Vellum issued a pinned state: a keyed digest of the state, the project
/// and a purpose, under a key derived from the instance master key. It is not a secret. A
/// caller cannot compute it, so a state that a caller made or changed (a promise taken out, a
/// commit invented) has no seal that verifies, and an export refuses it.
/// <para>
/// The seal names the master key it was made under. It verifies while that key is loaded, as
/// the current key or as the previous key. A seal of another instance, or of a key that was
/// removed after a rotation, does not verify. Nothing stores a seal, so a rotation has no
/// seal to move: an export of a state whose seal still verifies seals it under the current key.
/// </para>
/// </summary>
internal static class PinnedSeal
{
    /// <summary>The most characters a seal has. A longer value in a request is not a seal.</summary>
    public const int MaxLength = 128;

    private const string Version = "v1";
    private static readonly byte[] Context = "vellum.build.pinned-seal.v1"u8.ToArray();

    /// <summary>The seal of <paramref name="pin"/> for its project under the current master key: <c>v1.&lt;key ID&gt;.&lt;digest&gt;</c>.</summary>
    public static string Issue(MasterKeyRing keys, PinnedState pin) =>
        $"{Version}.{keys.Current.Id}.{Convert.ToHexStringLower(keys.Current.Digest(Context, Sealed(pin.ProjectId, pin)))}";

    /// <summary>True when <paramref name="pin"/> carries a seal that this instance issued for exactly this state of <paramref name="projectId"/>.</summary>
    public static bool Verifies(MasterKeyRing keys, Guid projectId, PinnedState pin)
    {
        if (pin.Seal is not { Length: > 0 and <= MaxLength } seal || seal.Split('.') is not [Version, var keyId, var hex])
            return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }
        return keys.Digest(keyId, Context, Sealed(projectId, pin)) is { } digest && CryptographicOperations.FixedTimeEquals(digest, given);
    }

    /// <summary>What the digest is of: the purpose, the project, and the state in canonical JSON with no seal.</summary>
    private static byte[] Sealed(Guid projectId, PinnedState pin) =>
        [.. Encoding.UTF8.GetBytes($"vellum pinned state\n{projectId:D}\n"), .. BuildText.CanonicalJson(pin with { Seal = null })];
}
