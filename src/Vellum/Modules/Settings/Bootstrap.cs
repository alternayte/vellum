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
/// master key, the cookie policy and the setup token. Every other setting lives in the app.
/// </summary>
public sealed class Bootstrap
{
    public const string MasterKeyFileName = "master.key";
    public const int MinimumSetupTokenLength = 16;

    private Bootstrap(OperatingProfile profile, MasterKeyRing keys, bool secureCookies, string? setupToken)
    {
        Profile = profile;
        Keys = keys;
        SecureCookies = secureCookies;
        SetupToken = setupToken;
    }

    public OperatingProfile Profile { get; }
    public string ProfileName => Profile == OperatingProfile.Hosted ? "hosted" : "local";
    public MasterKeyRing Keys { get; }
    public bool SecureCookies { get; }
    public string? SetupToken { get; }

    /// <summary>First-run setup needs the token in the hosted profile, and in local when one is set.</summary>
    public bool RequiresSetupToken => Profile == OperatingProfile.Hosted || SetupToken is not null;

    /// <summary>
    /// For the build-time OpenAPI generator only: that process has no database and must not
    /// write a key file. The key exists for the life of the process.
    /// </summary>
    public static Bootstrap Ephemeral() =>
        new(OperatingProfile.Local, new MasterKeyRing(new MasterKey(RandomNumberGenerator.GetBytes(MasterKey.Size))), false, null);

    /// <exception cref="BootstrapException">A value is malformed, or the hosted profile is not safe.</exception>
    public static Bootstrap Load(IConfiguration configuration)
    {
        var profile = configuration["Vellum:Profile"]?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local" => OperatingProfile.Local,
            "hosted" => OperatingProfile.Hosted,
            _ => throw new BootstrapException("Vellum__Profile is not valid. Set it to 'local' or 'hosted'."),
        };

        var current = ReadKey(configuration, "MasterKey");
        var previous = ReadKey(configuration, "PreviousMasterKey");

        if (current is null)
        {
            if (profile == OperatingProfile.Hosted)
                throw new BootstrapException(
                    "The hosted profile needs a master key. Set Vellum__MasterKey to the base64 form of 32 random bytes " +
                    "(for example the output of 'openssl rand -base64 32'), or set Vellum__MasterKeyFile to a file that holds that value.");

            var directory = configuration["Vellum:DataDirectory"];
            current = LoadOrCreateKeyFile(string.IsNullOrWhiteSpace(directory) ? "./data" : directory);
        }

        var secureCookies = ReadSecureCookies(configuration, profile);
        var setupToken = configuration["Vellum:SetupToken"]?.Trim();
        if (string.IsNullOrEmpty(setupToken))
            setupToken = null;

        if (profile == OperatingProfile.Hosted)
        {
            if (!secureCookies)
                throw new BootstrapException(
                    "The hosted profile sends the session cookie over HTTPS only. Remove Vellum__SecureCookies=false and serve the app over HTTPS.");

            var hosts = (configuration["AllowedHosts"] ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hosts.Length == 0 || hosts.Contains("*"))
                throw new BootstrapException(
                    "The hosted profile does not accept every host name. Set AllowedHosts to the public host names of this instance, separated by ';'.");

            if (setupToken is not null && setupToken.Length < MinimumSetupTokenLength)
                throw new BootstrapException(
                    $"Vellum__SetupToken is too short. Set it to at least {MinimumSetupTokenLength} random characters.");
        }

        return new Bootstrap(profile, new MasterKeyRing(current, previous), secureCookies, setupToken);
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

        string text;
        try
        {
            text = File.ReadAllText(file!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootstrapException(
                $"Vellum__{name}File names a file that cannot be read ({ex.GetType().Name}). Mount the file and make it readable by the app user.");
        }

        return Decode(text, $"the file that Vellum__{name}File names");
    }

    // The message never repeats the value.
    private static MasterKey Decode(string text, string source)
    {
        var buffer = new byte[MasterKey.Size + 1];
        if (!Convert.TryFromBase64String(text.Trim(), buffer, out var written) || written != MasterKey.Size)
            throw new BootstrapException(
                $"The master key in {source} is not valid. It must be the base64 form of exactly {MasterKey.Size} bytes (for example the output of 'openssl rand -base64 32').");
        try
        {
            return new MasterKey(buffer.AsSpan(0, MasterKey.Size));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// The local profile keeps its key beside its data so a restart reads the same key.
    /// The file is created with "fail if it exists", which the file system does atomically,
    /// so two instances that start together end with one key: one creates the file and the
    /// other reads it.
    /// </summary>
    private static MasterKey LoadOrCreateKeyFile(string directory)
    {
        var path = Path.Combine(Path.GetFullPath(directory), MasterKeyFileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            try
            {
                using var stream = new FileStream(path, options);
                stream.Write(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(MasterKey.Size))));
            }
            catch (IOException) when (File.Exists(path))
            {
                // The file is there: from an earlier start, or from an instance that is
                // creating it at this moment.
            }

            // The creator writes the key in one call right after it creates the file. A
            // reader that arrives in between sees an empty file and reads again.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var text = ReadShared(path);
                if (text.Length > 0)
                    return Decode(text, $"the generated key file '{path}'");
                Thread.Sleep(50);
            }

            throw new BootstrapException(
                $"The master key file '{path}' is empty. If this instance has no stored secrets yet, delete the file and start again. " +
                "Otherwise restore the file from a backup.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootstrapException(
                $"The master key file '{path}' cannot be read or created ({ex.GetType().Name}). " +
                "Make Vellum__DataDirectory a directory that the app user can write, or set Vellum__MasterKey.");
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
