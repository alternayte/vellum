using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using Vellum.Modules.Settings.Crypto;

namespace Vellum.Modules.Settings;

public enum OperatingProfile
{
    Local,
    Hosted
}

/// <summary>The environment is not safe to start with. The message states the fix.</summary>
public sealed class BootstrapException(string message) : Exception(message);

/// <summary>
/// The values that come from the environment and nothing else: the operating profile, the
/// master key, the cookie policy, the setup token and the data directory. Every other
/// setting lives in the app.
/// </summary>
public sealed class Bootstrap
{
    public const string MasterKeyFileName = "master.key";
    public const int MinimumSetupTokenLength = 16;

    private Bootstrap(
        OperatingProfile profile, MasterKeyRing keys, bool secureCookies, string? setupToken, string dataDirectory, string? unusedKeyFile)
    {
        Profile = profile;
        Keys = keys;
        SecureCookies = secureCookies;
        SetupToken = setupToken;
        DataDirectory = dataDirectory;
        UnusedKeyFile = unusedKeyFile;
    }

    public OperatingProfile Profile { get; }
    public string ProfileName => Profile == OperatingProfile.Hosted ? "hosted" : "local";
    public MasterKeyRing Keys { get; }
    public bool SecureCookies { get; }

    /// <summary>The setup token from the environment, or null when the operator set none.</summary>
    public string? SetupToken { get; }

    /// <summary>Where the local profile keeps its generated key and its setup code.</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// A generated key file that exists and is not read, because the environment gives the
    /// master key. The file never takes over without a notice: if the environment key is
    /// removed later, the start-up key check refuses the file's key while the database holds
    /// rows under another key.
    /// </summary>
    public string? UnusedKeyFile { get; }

    /// <summary>
    /// For the build-time OpenAPI generator only: that process has no database and must not
    /// write a key file. The key exists for the life of the process.
    /// </summary>
    public static Bootstrap Ephemeral()
    {
        Span<byte> key = stackalloc byte[MasterKey.Size];
        RandomNumberGenerator.Fill(key);
        try
        {
            return new(OperatingProfile.Local, new MasterKeyRing(new MasterKey(key)), false, null, Path.GetFullPath("./data"), null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <param name="keysInUse">
    /// The master keys that rows in the database are encrypted under. It is called only when
    /// the local profile is about to create a key file: a new key is never created while the
    /// database holds rows under another key.
    /// </param>
    /// <exception cref="BootstrapException">A value is malformed, or the environment is not safe.</exception>
    public static Bootstrap Load(IConfiguration configuration, Func<IReadOnlyList<MasterKeyUsage>> keysInUse)
    {
        var profile = configuration["Vellum:Profile"]?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local" => OperatingProfile.Local,
            "hosted" => OperatingProfile.Hosted,
            _ => throw new BootstrapException("Vellum__Profile is not valid. Set it to 'local' or 'hosted'."),
        };

        var current = ReadKey(configuration, "MasterKey");
        var previous = ReadKey(configuration, "PreviousMasterKey");
        var dataDirectory = ResolveDataDirectory(configuration);
        var keyFile = Path.Combine(dataDirectory, MasterKeyFileName);
        string? unusedKeyFile = null;

        if (current is null)
        {
            if (profile == OperatingProfile.Hosted)
                throw new BootstrapException(
                    "The hosted profile needs a master key. Set Vellum__MasterKey to the base64 form of 32 random bytes " +
                    "(for example the output of 'openssl rand -base64 32'), or set Vellum__MasterKeyFile to a file that holds that value.");

            current = LoadOrCreateKeyFile(keyFile, keysInUse);
        }
        else if (profile == OperatingProfile.Local && File.Exists(keyFile))
        {
            unusedKeyFile = keyFile;
        }

        var secureCookies = ReadSecureCookies(configuration, profile);
        var setupToken = configuration["Vellum:SetupToken"]?.Trim();
        if (string.IsNullOrEmpty(setupToken))
            setupToken = null;
        if (setupToken is not null && setupToken.Length < MinimumSetupTokenLength)
            throw new BootstrapException(
                $"Vellum__SetupToken is too short. Set it to at least {MinimumSetupTokenLength} random characters. " +
                "In the local profile you can also remove it; Vellum then generates a setup code.");

        if (profile == OperatingProfile.Hosted)
        {
            if (!secureCookies)
                throw new BootstrapException(
                    "The hosted profile sends the session cookie over HTTPS only. Remove Vellum__SecureCookies=false and serve the app over HTTPS.");

            // ASP.NET host filtering reads each of these entries as "every host name", in any
            // position of the list. An empty entry has no meaning and hides a typing error.
            var hosts = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.TrimEntries);
            if (hosts.Any(h => h.Length == 0 || h.Contains('*') || h is "0.0.0.0" or "::" or "[::]"))
                throw new BootstrapException(
                    "The hosted profile does not accept every host name. Set AllowedHosts to the public host names of this instance, separated by ';'. " +
                    "Do not use '*', a name that contains '*', '0.0.0.0', '::', '[::]' or an empty entry.");
        }

        return new Bootstrap(profile, new MasterKeyRing(current, previous), secureCookies, setupToken, dataDirectory, unusedKeyFile);
    }

    /// <summary>
    /// For maintenance commands: the IDs of the master keys that the environment gives, with
    /// no profile checks. It reads an existing key file and never creates one.
    /// </summary>
    public static IReadOnlyList<string> AvailableKeyIds(IConfiguration configuration)
    {
        var ids = new List<string>();
        var keyFile = Path.Combine(ResolveDataDirectory(configuration), MasterKeyFileName);
        if (ReadKey(configuration, "MasterKey") is { } current)
            ids.Add(current.Id);
        else if (File.Exists(keyFile))
            ids.Add(ReadKeyFile(keyFile).Id);
        if (ReadKey(configuration, "PreviousMasterKey") is { } previous)
            ids.Add(previous.Id);
        return ids;
    }

    private static string ResolveDataDirectory(IConfiguration configuration)
    {
        var directory = configuration["Vellum:DataDirectory"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "./data" : directory);
    }

    private static bool ReadSecureCookies(IConfiguration configuration, OperatingProfile profile)
    {
        var value = configuration["Vellum:SecureCookies"];
        if (string.IsNullOrWhiteSpace(value))
            return profile == OperatingProfile.Hosted;
        return bool.TryParse(value.Trim(), out var parsed)
            ? parsed
            : throw new BootstrapException("Vellum__SecureCookies is not valid. Set it to 'true' or 'false'.");
    }

    private static MasterKey? ReadKey(IConfiguration configuration, string name)
    {
        var inline = configuration[$"Vellum:{name}"];
        var file = configuration[$"Vellum:{name}File"];
        var hasInline = !string.IsNullOrWhiteSpace(inline);
        var hasFile = !string.IsNullOrWhiteSpace(file);

        if (hasInline && hasFile)
            throw new BootstrapException($"Vellum__{name} and Vellum__{name}File are both set. Set one of them.");
        if (hasInline)
            return Decode(inline!, $"Vellum__{name}");
        if (!hasFile)
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootstrapException(
                $"Vellum__{name}File names a file that cannot be read ({ex.GetType().Name}). Mount the file and make it readable by the app user.");
        }

        return DecodeAndClear(bytes, $"the file that Vellum__{name}File names");
    }

    // The message never repeats the value. A configuration value is a string, which the
    // runtime owns. Every buffer that this class makes for a key is cleared after use.
    private static MasterKey Decode(string text, string source)
    {
        Span<byte> buffer = stackalloc byte[MasterKey.Size + 1];
        try
        {
            return Convert.TryFromBase64String(text.Trim(), buffer, out var written) && written == MasterKey.Size
                ? new MasterKey(buffer[..MasterKey.Size])
                : throw Malformed(source);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>Decodes the base64 text in <paramref name="utf8"/> and clears it.</summary>
    private static MasterKey DecodeAndClear(byte[] utf8, string source)
    {
        Span<byte> buffer = stackalloc byte[MasterKey.Size + 1];
        try
        {
            var text = utf8.AsSpan().Trim(" \t\r\n"u8);
            return Base64.DecodeFromUtf8(text, buffer, out var consumed, out var written) == OperationStatus.Done
                   && consumed == text.Length && written == MasterKey.Size
                ? new MasterKey(buffer[..MasterKey.Size])
                : throw Malformed(source);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private static BootstrapException Malformed(string source) => new(
        $"The master key in {source} is not valid. It must be the base64 form of exactly {MasterKey.Size} bytes (for example the output of 'openssl rand -base64 32').");

    /// <summary>
    /// The local profile keeps its key beside its data so a restart reads the same key.
    /// The file is created with "fail if it exists", which the file system does atomically,
    /// so two instances that start together end with one key: one creates the file and the
    /// other reads it. No file is created while the database holds rows under another key:
    /// a new key can read none of them.
    /// </summary>
    private static MasterKey LoadOrCreateKeyFile(string path, Func<IReadOnlyList<MasterKeyUsage>> keysInUse)
    {
        try
        {
            if (!File.Exists(path))
            {
                var inUse = keysInUse();
                if (inUse.Count > 0)
                    throw new BootstrapException(
                        $"The master key file '{path}' does not exist, and the database holds rows that are encrypted under another master key: " +
                        $"{MasterKeyGuard.Describe(inUse)}. Vellum does not create a new key, because a new key cannot read these rows. " +
                        "Restore the key file from a backup, or set Vellum__MasterKey to the key that was used. " +
                        "If the key is lost and you accept the loss of these rows, run 'dotnet Vellum.dll keys forget --yes' and start again.");

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (TryCreateKeyFile(path) is { } created)
                    return created;
            }

            return ReadKeyFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootstrapException(
                $"The master key file '{path}' cannot be read or created ({ex.GetType().Name}). " +
                "Make Vellum__DataDirectory a directory that the app user can write, or set Vellum__MasterKey.");
        }
    }

    /// <returns>The new key, or null when another instance created the file first.</returns>
    private static MasterKey? TryCreateKeyFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }

        Span<byte> key = stackalloc byte[MasterKey.Size];
        Span<byte> text = stackalloc byte[Base64.GetMaxEncodedToUtf8Length(MasterKey.Size)];
        try
        {
            RandomNumberGenerator.Fill(key);
            Base64.EncodeToUtf8(key, text, out _, out var written);
            stream.Write(text[..written]);
            return new MasterKey(key);
        }
        finally
        {
            stream.Dispose();
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(text);
        }
    }

    private static MasterKey ReadKeyFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & others) != 0)
                throw new BootstrapException(
                    $"The master key file '{path}' can be read or changed by other users of this machine. " +
                    $"Run 'chmod 600 {path}' and start again. If another user can have read the key, rotate the master key.");
        }

        // The creator writes the key in one call right after it creates the file. A
        // reader that arrives in between sees an empty file and reads again.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var bytes = ReadShared(path);
            if (bytes.AsSpan().Trim(" \t\r\n"u8).Length > 0)
                return DecodeAndClear(bytes, $"the generated key file '{path}'");
            Thread.Sleep(50);
        }

        throw new BootstrapException(
            $"The master key file '{path}' is empty. If this instance has no stored secrets yet, delete the file and start again. " +
            "Otherwise restore the file from a backup.");
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[checked((int)Math.Min(stream.Length, 4096))];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
