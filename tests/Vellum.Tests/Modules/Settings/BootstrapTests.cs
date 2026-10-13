using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Crypto;

namespace Vellum.Tests.Modules.Settings;

public class BootstrapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vellum-bootstrap-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static string NewKey() => TestHost.NewMasterKey();

    [Fact]
    public void Hosted_without_a_master_key_refuses_and_states_the_fix()
    {
        var ex = Assert.Throws<BootstrapException>(() =>
            Bootstrap.Load(Config(("Vellum:Profile", "hosted"), ("AllowedHosts", "vellum.example.com"), ("Vellum:DataDirectory", _directory))));

        Assert.Contains("Vellum__MasterKey", ex.Message);
        Assert.Contains("openssl rand -base64 32", ex.Message);
        // The hosted profile never falls back to a generated key.
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("vellum.example.com;*")]
    public void Hosted_with_any_host_allowed_refuses(string? allowedHosts)
    {
        var ex = Assert.Throws<BootstrapException>(() =>
            Bootstrap.Load(Config(("Vellum:Profile", "hosted"), ("Vellum:MasterKey", NewKey()), ("AllowedHosts", allowedHosts))));
        Assert.Contains("AllowedHosts", ex.Message);
    }

    [Fact]
    public void Hosted_with_cookies_not_marked_secure_refuses()
    {
        var ex = Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(
            ("Vellum:Profile", "hosted"), ("Vellum:MasterKey", NewKey()), ("AllowedHosts", "vellum.example.com"), ("Vellum:SecureCookies", "false"))));
        Assert.Contains("Vellum__SecureCookies", ex.Message);
    }

    [Fact]
    public void Hosted_with_a_short_setup_token_refuses()
    {
        var ex = Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(
            ("Vellum:Profile", "hosted"), ("Vellum:MasterKey", NewKey()), ("AllowedHosts", "vellum.example.com"), ("Vellum:SetupToken", "short"))));
        Assert.Contains("Vellum__SetupToken", ex.Message);
    }

    [Fact]
    public void A_safe_hosted_bootstrap_loads_with_secure_cookies()
    {
        var bootstrap = Bootstrap.Load(Config(
            ("Vellum:Profile", "Hosted"), ("Vellum:MasterKey", NewKey()), ("AllowedHosts", "vellum.example.com"), ("Vellum:SetupToken", TestHost.SetupToken)));

        Assert.Equal(OperatingProfile.Hosted, bootstrap.Profile);
        Assert.Equal("hosted", bootstrap.ProfileName);
        Assert.True(bootstrap.SecureCookies);
        Assert.True(bootstrap.RequiresSetupToken);
        Assert.False(bootstrap.Keys.HasPrevious);
    }

    [Fact]
    public void Local_has_no_refusals_and_generates_a_key_file_that_it_reuses()
    {
        var config = Config(("Vellum:DataDirectory", _directory), ("AllowedHosts", "*"), ("Vellum:SecureCookies", "false"));

        var first = Bootstrap.Load(config);
        var path = Path.Combine(_directory, Bootstrap.MasterKeyFileName);

        Assert.Equal(OperatingProfile.Local, first.Profile);
        Assert.False(first.SecureCookies);
        Assert.False(first.RequiresSetupToken);
        Assert.True(File.Exists(path));
        Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(path)).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var written = File.ReadAllText(path);
        var second = Bootstrap.Load(config);

        Assert.Equal(first.Keys.Current.Id, second.Keys.Current.Id);
        Assert.Equal(written, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Instances_that_start_together_end_with_one_key()
    {
        var config = Config(("Vellum:DataDirectory", _directory));

        var ids = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => Bootstrap.Load(config).Keys.Current.Id).Distinct().ToList();

        Assert.Single(ids);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void A_key_file_and_a_previous_key_are_read()
    {
        Directory.CreateDirectory(_directory);
        var key = NewKey();
        var previous = NewKey();
        var keyFile = Path.Combine(_directory, "current");
        var previousFile = Path.Combine(_directory, "previous");
        File.WriteAllText(keyFile, key + "\n");
        File.WriteAllText(previousFile, previous);

        var fromFiles = Bootstrap.Load(Config(("Vellum:MasterKeyFile", keyFile), ("Vellum:PreviousMasterKeyFile", previousFile)));
        var inline = Bootstrap.Load(Config(("Vellum:MasterKey", key), ("Vellum:PreviousMasterKey", previous)));

        Assert.Equal(inline.Keys.Current.Id, fromFiles.Keys.Current.Id);
        Assert.True(fromFiles.Keys.HasPrevious);
        Assert.True(inline.Keys.HasPrevious);
        Assert.Equal(16, inline.Keys.Current.Id.Length);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void A_malformed_master_key_refuses_without_repeating_it(string value)
    {
        var ex = Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(("Vellum:MasterKey", value))));
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void A_master_key_given_twice_or_as_a_missing_file_refuses()
    {
        Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(
            ("Vellum:MasterKey", NewKey()), ("Vellum:MasterKeyFile", Path.Combine(_directory, "k")))));
        Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(
            ("Vellum:MasterKeyFile", Path.Combine(_directory, "missing")))));
        Assert.Throws<BootstrapException>(() => Bootstrap.Load(Config(("Vellum:Profile", "production"))));
    }

    // ---- Envelope ------------------------------------------------------------

    private static MasterKeyRing Ring(byte[] current, byte[]? previous = null) =>
        new(new MasterKey(current), previous is null ? null : new MasterKey(previous));

    [Fact]
    public void A_sealed_value_opens_only_with_its_key_and_its_associated_data()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var ring = Ring(key);
        var plaintext = Encoding.UTF8.GetBytes("sk-live-0123456789");
        var place = "workspace-a|github:token"u8.ToArray();

        var first = ring.Seal(plaintext, place);
        var second = ring.Seal(plaintext, place);

        Assert.Equal(plaintext, ring.Open(first, place));
        // Each value has its own data key and nonces.
        Assert.NotEqual(first.WrappedKey, second.WrappedKey);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(ring.Current.Id, first.MasterKeyId);

        Assert.ThrowsAny<CryptographicException>(() => ring.Open(first, "workspace-b|github:token"u8.ToArray()));
        Assert.ThrowsAny<CryptographicException>(() => ring.Open(first with { Ciphertext = Flip(first.Ciphertext) }, place));
        Assert.ThrowsAny<CryptographicException>(() => ring.Open(first with { WrappedKey = Flip(first.WrappedKey) }, place));
        // A data key from another value does not open this one.
        Assert.ThrowsAny<CryptographicException>(() => ring.Open(first with { WrappedKey = second.WrappedKey, WrapNonce = second.WrapNonce }, place));
        Assert.Throws<MasterKeyUnavailableException>(() => Ring(RandomNumberGenerator.GetBytes(32)).Open(first, place));
    }

    [Fact]
    public void Rewrap_moves_a_value_to_the_current_key_and_the_previous_key_is_then_not_needed()
    {
        var oldKey = RandomNumberGenerator.GetBytes(32);
        var newKey = RandomNumberGenerator.GetBytes(32);
        var place = "p"u8.ToArray();
        var value = Ring(oldKey).Seal("secret"u8, place);

        var rotating = Ring(newKey, oldKey);
        Assert.Equal("secret"u8.ToArray(), rotating.Open(value, place));
        Assert.False(rotating.IsCurrent(value.MasterKeyId));

        var rewrapped = rotating.Rewrap(value, place);

        Assert.True(rotating.IsCurrent(rewrapped.MasterKeyId));
        Assert.Equal(value.Ciphertext, rewrapped.Ciphertext);
        Assert.Equal("secret"u8.ToArray(), Ring(newKey).Open(rewrapped, place));
        Assert.Throws<MasterKeyUnavailableException>(() => Ring(oldKey).Open(rewrapped, place));
        Assert.Throws<MasterKeyUnavailableException>(() => Ring(newKey).Rewrap(value, place));
    }

    private static byte[] Flip(byte[] bytes)
    {
        var copy = bytes.ToArray();
        copy[0] ^= 0x01;
        return copy;
    }
}
