using System.Security.Cryptography;

namespace Vellum.Modules.Settings.Crypto;

/// <summary>A 256-bit key from the environment. It wraps data keys; it never encrypts a value.</summary>
public sealed class MasterKey
{
    public const int Size = 32;

    private readonly byte[] _key;

    public MasterKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Size)
            throw new ArgumentException($"A master key is {Size} bytes.", nameof(key));
        // The one copy of the key that the process keeps. It is pinned, so the collector
        // never moves it and leaves no stale copy behind.
        _key = GC.AllocateUninitializedArray<byte>(Size, pinned: true);
        key.CopyTo(_key);
        // A keyed digest: the ID names the key in rows and diagnostics and says nothing about it.
        Id = Convert.ToHexStringLower(HMACSHA256.HashData(_key, "vellum.master-key-id"u8)[..8]);
    }

    public string Id { get; }

    /// <summary>
    /// A keyed digest of <paramref name="value"/> under a key that is derived from this key
    /// for <paramref name="context"/>. Equal values in two contexts give different digests,
    /// and a reader of the digest cannot test a guess without this key.
    /// </summary>
    internal byte[] Digest(ReadOnlySpan<byte> context, ReadOnlySpan<byte> value)
    {
        Span<byte> derived = stackalloc byte[Size];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, derived, salt: [], info: context);
            return HMACSHA256.HashData(derived, value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    internal byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData) =>
        Envelope.Encrypt(_key, nonce, plaintext, associatedData);

    internal byte[] Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData) =>
        Envelope.Decrypt(_key, nonce, ciphertext, associatedData);
}

/// <summary>
/// One encrypted value. <see cref="WrappedKey"/> is the value's own data key, encrypted with
/// the master key named by <see cref="MasterKeyId"/>. Each ciphertext ends with its 16-byte tag.
/// </summary>
public sealed record SealedValue(string MasterKeyId, byte[] WrapNonce, byte[] WrappedKey, byte[] Nonce, byte[] Ciphertext);

/// <summary>The master key is not loaded, so nothing it wrapped can be read.</summary>
public sealed class MasterKeyUnavailableException(string masterKeyId)
    : Exception($"Master key {masterKeyId} is not loaded.")
{
    public string MasterKeyId { get; } = masterKeyId;
}

/// <summary>
/// Envelope encryption with AES-256-GCM. New values are sealed with the current master key.
/// The previous key, when loaded, only unwraps.
/// </summary>
public sealed class MasterKeyRing
{
    private readonly MasterKey? _previous;

    public MasterKeyRing(MasterKey current, MasterKey? previous = null)
    {
        Current = current;
        _previous = previous is not null && previous.Id != current.Id ? previous : null;
    }

    public MasterKey Current { get; }
    public bool HasPrevious => _previous is not null;

    /// <summary>The IDs of the keys that can unwrap: the current key and the previous key.</summary>
    public IReadOnlyList<string> LoadedIds => _previous is null ? [Current.Id] : [Current.Id, _previous.Id];

    public bool IsLoaded(string masterKeyId) => masterKeyId == Current.Id || masterKeyId == _previous?.Id;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with a new random data key. The associated data
    /// binds the value to its place: a sealed value opens only with the same associated data.
    /// </summary>
    public SealedValue Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        Span<byte> dataKey = stackalloc byte[MasterKey.Size];
        RandomNumberGenerator.Fill(dataKey);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(Envelope.NonceSize);
            var ciphertext = Envelope.Encrypt(dataKey, nonce, plaintext, Envelope.DataScope(associatedData));
            var wrapNonce = RandomNumberGenerator.GetBytes(Envelope.NonceSize);
            var wrapped = Current.Encrypt(wrapNonce, dataKey, Envelope.WrapScope(associatedData));
            return new SealedValue(Current.Id, wrapNonce, wrapped, nonce, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <exception cref="MasterKeyUnavailableException">The wrapping master key is not loaded.</exception>
    /// <exception cref="CryptographicException">The value, its key or its associated data was changed.</exception>
    public byte[] Open(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        var dataKey = Unwrap(value, associatedData);
        try
        {
            return Envelope.Decrypt(dataKey, value.Nonce, value.Ciphertext, Envelope.DataScope(associatedData));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Wraps the value's data key with the current master key. The ciphertext does not change.</summary>
    public SealedValue Rewrap(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        var dataKey = Unwrap(value, associatedData);
        try
        {
            var wrapNonce = RandomNumberGenerator.GetBytes(Envelope.NonceSize);
            var wrapped = Current.Encrypt(wrapNonce, dataKey, Envelope.WrapScope(associatedData));
            return value with { MasterKeyId = Current.Id, WrapNonce = wrapNonce, WrappedKey = wrapped };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public bool IsCurrent(string masterKeyId) => masterKeyId == Current.Id;

    /// <summary>
    /// Proves that the wrapped key and the associated data are the ones that were sealed,
    /// without decrypting the value.
    /// </summary>
    /// <exception cref="MasterKeyUnavailableException">The wrapping master key is not loaded.</exception>
    /// <exception cref="CryptographicException">The wrapped key or its associated data was changed.</exception>
    public void Verify(SealedValue value, ReadOnlySpan<byte> associatedData) =>
        CryptographicOperations.ZeroMemory(Unwrap(value, associatedData));

    private byte[] Unwrap(SealedValue value, ReadOnlySpan<byte> associatedData)
    {
        var key = value.MasterKeyId == Current.Id ? Current
            : value.MasterKeyId == _previous?.Id ? _previous
            : throw new MasterKeyUnavailableException(value.MasterKeyId);
        return key.Decrypt(value.WrapNonce, value.WrappedKey, Envelope.WrapScope(associatedData));
    }
}

internal static class Envelope
{
    public const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (nonce.Length != NonceSize || ciphertext.Length < TagSize)
            throw new CryptographicException("The encrypted value is malformed.");
        var length = ciphertext.Length - TagSize;
        var output = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext[..length], ciphertext[length..], output, associatedData);
        return output;
    }

    // The data key and the value are bound to the same place under different labels, so a
    // wrapped key can never be presented as a value or the reverse.
    public static byte[] WrapScope(ReadOnlySpan<byte> associatedData) => Scope("wrap:"u8, associatedData);
    public static byte[] DataScope(ReadOnlySpan<byte> associatedData) => Scope("data:"u8, associatedData);

    private static byte[] Scope(ReadOnlySpan<byte> label, ReadOnlySpan<byte> associatedData)
    {
        var scoped = new byte[label.Length + associatedData.Length];
        label.CopyTo(scoped);
        associatedData.CopyTo(scoped.AsSpan(label.Length));
        return scoped;
    }
}
