using System.Security.Cryptography;
using System.Text;

namespace Vellum.Modules.Settings.Setup;

/// <summary>
/// The proof of operator access that first-run setup needs, in both profiles. It is the
/// setup token from the environment when one is set. Otherwise the local profile generates a
/// one-time setup code when it starts with no user: the code goes to the log and to a file
/// in the data directory, which only a person with access to the machine can read.
/// </summary>
public sealed class SetupAccess(Bootstrap bootstrap, ILogger<SetupAccess> log)
{
    public const string CodeFileName = "setup-code";

    /// <summary>The line in the log that carries the generated code contains this text.</summary>
    public const string LogMarker = "VELLUM SETUP CODE:";

    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private string? _generated;

    public string CodeFilePath => Path.Combine(bootstrap.DataDirectory, CodeFileName);

    /// <summary><c>operator</c>: the token is in the environment. <c>log</c>: the code is in the log and in the code file.</summary>
    public string Hint => bootstrap.SetupToken is not null ? "operator" : "log";

    /// <summary>
    /// Runs once before the host accepts requests.
    /// </summary>
    /// <exception cref="BootstrapException">
    /// A hosted instance has no user and no setup token, or the code file cannot be written.
    /// </exception>
    public void Prepare(bool needsSetup)
    {
        if (!needsSetup || bootstrap.SetupToken is not null)
        {
            // A code from an earlier start has no use now.
            RemoveCodeFile();
            return;
        }

        // A hosted instance with no user and no setup token would give the admin account to
        // the first caller, so it does not start.
        if (bootstrap.Profile == OperatingProfile.Hosted)
            throw new BootstrapException(
                "The hosted profile has no user yet and no setup token. Set Vellum__SetupToken to at least " +
                $"{Bootstrap.MinimumSetupTokenLength} random characters and give it to the person who runs first-time setup.");

        try
        {
            _generated = LoadOrCreateCode();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootstrapException(
                $"The setup code file '{CodeFilePath}' cannot be read or created ({ex.GetType().Name}). " +
                "Make Vellum__DataDirectory a directory that the app user can write, or set Vellum__SetupToken.");
        }

        log.LogWarning(
            "{Marker} {Code}  Type this code in the first-run setup screen. It works one time. It is also in the file {Path}.",
            LogMarker, _generated, CodeFilePath);
    }

    /// <summary>Compares in constant time. Both sides are hashed first, so the length of the input does not show.</summary>
    public bool Matches(string? supplied)
    {
        var expected = bootstrap.SetupToken ?? _generated;
        if (expected is null || string.IsNullOrEmpty(supplied))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Trim())));
    }

    /// <summary>
    /// Setup is done: the code file is deleted. The code needs no reset in memory, because
    /// setup answers "already set up" before it looks at a code once a user exists. Calls
    /// that raced the one that won still hold a valid code and get that answer too.
    /// </summary>
    public void Complete() => RemoveCodeFile();

    private void RemoveCodeFile()
    {
        try
        {
            File.Delete(CodeFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The code in the file is void once a user exists. The file is only clutter.
            log.LogWarning(ex, "The setup code file {Path} could not be deleted. Delete it by hand.", CodeFilePath);
        }
    }

    /// <summary>
    /// Instances that share a data directory share one code: the file is created with "fail
    /// if it exists", and the instance that loses reads the winner's code.
    /// </summary>
    private string LoadOrCreateCode()
    {
        Directory.CreateDirectory(bootstrap.DataDirectory);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using var stream = new FileStream(CodeFilePath, options);
            var code = NewCode();
            stream.Write(Encoding.ASCII.GetBytes(code + "\n"));
            return code;
        }
        catch (IOException) when (File.Exists(CodeFilePath))
        {
            // The file is there from an earlier start, or another instance is writing it now.
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            using (var stream = new FileStream(CodeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                var code = reader.ReadToEnd().Trim();
                if (code.Length >= Bootstrap.MinimumSetupTokenLength)
                    return code;
            }

            Thread.Sleep(50);
        }

        throw new BootstrapException($"The setup code file '{CodeFilePath}' is empty or too short. Delete the file and start again.");
    }

    // 20 characters from 31, about 99 bits, in groups that a person can type.
    private static string NewCode()
    {
        var characters = RandomNumberGenerator.GetString(CodeAlphabet, 20);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => characters.Substring(i * 5, 5)));
    }
}
