using System.Diagnostics;
using Vellum.Modules.Ai;

namespace Vellum.Tests.Modules.Ai;

/// <summary>What the redactor removes, what it must leave readable and that hostile text cannot make it slow.</summary>
public class SecretRedactorTests
{
    [Theory]
    // Assignments whose key holds a credential word, with prefixes and suffixes.
    [InlineData("SECRET_KEY_BASE=3b1f0c9d8e7a6b5c4d3e2f1a", "3b1f0c9d8e7a")]
    [InlineData("DB_PASS=hunter2", "hunter2")]
    [InlineData("dbPass: hunter2", "hunter2")]
    [InlineData("PGPASSWORD=correct horse battery staple", "horse battery staple")]
    [InlineData("passphrase: open sesame now", "sesame")]
    [InlineData("pwd=letmein", "letmein")]
    [InlineData("MYSQL_PWD = letmein", "letmein")]
    [InlineData("basic_auth: dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("ENCRYPTION_KEY=q83vEjRWeJq83vEjRWeJq83v", "q83vEjRWeJq8")]
    [InlineData("encryption-key: \"two words\"", "two words")]
    [InlineData("webhook_signature: t=1492774577,v1=5257a869e7ec", "5257a869e7ec")]
    [InlineData("sig=aGVsbG8gd29ybGQ", "aGVsbG8gd29ybGQ")]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS_JSON={\"private_key_id\":\"abc123def\"}", "abc123def")]
    [InlineData("token := \"s3cr3t-value\"", "s3cr3t-value")]
    [InlineData("  api-key : 'k-1234'", "k-1234")]
    [InlineData("{\"a\":1,\"clientSecret\":\"first one\",\"b\":2,\"refreshToken\":\"second one\"}", "second one")]
    [InlineData("{\"a\":1,\"clientSecret\":\"first one\",\"b\":2,\"refreshToken\":\"second one\"}", "first one")]
    [InlineData("password: \"with \\\" quote inside\"", "quote inside")]
    // Bearer tokens anywhere in a line.
    [InlineData("curl -H 'X-Upstream: Bearer abcdef0123456789' https://api.example", "abcdef0123456789")]
    [InlineData("the service sends bearer AbCdEf0123456789xyz to the gateway", "AbCdEf0123456789xyz")]
    // URLs that carry user information.
    [InlineData("redis://:r3d1sP4ss@cache.internal:6379/0", "r3d1sP4ss")]
    [InlineData("https://deploy:gh0st@git.example.com/acme/orders.git", "gh0st")]
    [InlineData("mongodb+srv://app:m0ng0@cluster0.example.net/db", "m0ng0")]
    // Azure storage and SAS.
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=Zm9vYmFyYmF6cXV4Zm9vYmFyYmF6cXV4;EndpointSuffix=core.windows.net", "Zm9vYmFyYmF6cXV4")]
    [InlineData("SharedAccessSignature=sv=2022-11-02&ss=b&sig=AbCdEf123456%3D", "AbCdEf123456")]
    [InlineData("https://acme.blob.core.windows.net/c/b.txt?sv=2022-11-02&sr=b&sig=Q2hlY2tTaWduYXR1cmU%3D", "Q2hlY2tTaWduYXR1cmU")]
    // Webhook URLs are credentials.
    [InlineData("notify https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXXXXXX on failure", "XXXXXXXXXXXXXXXXXXXXXXXX")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcDEF-ghiJKL_mnoPQR", "abcDEF-ghiJKL_mnoPQR")]
    [InlineData("https://discordapp.com/api/v10/webhooks/123456789012345678/abcDEF-ghiJKL_mnoPQR", "abcDEF-ghiJKL_mnoPQR")]
    // Vendor token shapes with no key beside them.
    [InlineData("use sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx here", "AbCdEfGhIjKlMnOp")]
    [InlineData("gho_0123456789abcdefghijABCDEFGHIJ012345", "gho_0123456789")]
    [InlineData("ghu_0123456789abcdefghijABCDEFGHIJ012345", "ghu_0123456789")]
    [InlineData("ghs_0123456789abcdefghijABCDEFGHIJ012345", "ghs_0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789", "11ABCDEFG0abcdefghijkl")]
    [InlineData("glpat-AbCdEfGhIjKlMnOpQrSt", "AbCdEfGhIjKlMnOp")]
    [InlineData("xoxb-123456789012-abcdefghijkl", "123456789012-abcdefghijkl")]
    [InlineData("ASIAIOSFODNN7EXAMPLE", "ASIAIOSFODNN7EXAMPLE")]
    [InlineData("AIzaSyA-abcdefghijklmnopqrstuvwxyz01234", "SyA-abcdefghijklmnop")]
    [InlineData("ya29.a0AfH6SMBx-abcdefghijklmnopqrstuvwxyz", "a0AfH6SMBx-abcdefghij")]
    [InlineData("npm_abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("hf_abcdefghijklmnopqrstuvwxyzABCDEFGH", "abcdefghijklmnopqrstuvwxyzABCDEFGH")]
    [InlineData("SG.abcdefghijklmnopqrstuv.ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefg", "ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("sk_live_abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    [InlineData("rk_live_abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    [InlineData("pk_live_abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    // Cookies.
    [InlineData("Cookie: session=abc123; theme=dark", "abc123")]
    [InlineData("Set-Cookie: sid=s%3Aabc.def; Path=/; HttpOnly", "s%3Aabc.def")]
    // A key body whose BEGIN line is missing.
    [InlineData("MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7\nVJTUt9Us8cKjMzEfYyjiWA4R4ypbHqGCXk8PzJdVtqZ1x2y3\n-----END PRIVATE KEY-----", "VJTUt9Us8cKjMzEfYyjiWA4R")]
    [InlineData("MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7\nVJTUt9Us8cKjMzEfYyjiWA4R4ypbHqGCXk8PzJdVtqZ1x2y3\n-----END PRIVATE KEY-----", "MIIEvQIBADANBgkqhkiG9w0B")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----\n\nlQOYBF0abcdefghijklmnop\n-----END PGP PRIVATE KEY BLOCK-----", "lQOYBF0abcdefghijklmnop")]
    // A long random value assigned to a name that says nothing.
    [InlineData("thing = \"Zx9Kq2Lm8Vb4Nc7Pd1Rf6Tg3Yh5Uj0WaQeSi\"", "Zx9Kq2Lm8Vb4Nc7Pd1Rf")]
    [InlineData("VALUE=Zx9Kq2Lm8Vb4Nc7Pd1Rf6Tg3Yh5Uj0WaQeSi", "Zx9Kq2Lm8Vb4Nc7Pd1Rf")]
    public void A_credential_is_removed(string text, string secret)
    {
        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(SecretRedactor.Marker, redacted);
    }

    [Theory]
    // Provenance: a commit SHA, a SHA-256 and a UUID stay readable wherever they stand.
    [InlineData("commit = 9fceb02d0ae598e95dc970b74767f19372d61af8")]
    [InlineData("sha: \"9fceb02d0ae598e95dc970b74767f19372d61af8\"")]
    [InlineData("digest=sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("id: 123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("acme/orders@9fceb02d0ae598e95dc970b74767f19372d61af8:src/Orders/Program.cs:10-42")]
    // Words that hold a credential word by accident.
    [InlineData("author: Jane Doe")]
    [InlineData("design: hexagonal")]
    [InlineData("passed: 12 failed: 0")]
    [InlineData("compass = north")]
    [InlineData("signal: SIGTERM")]
    [InlineData("\"prompt_tokens\": 21, \"completion_tokens\": 9")]
    [InlineData("maxOutputTokens: 4096")]
    // Comparisons, lambdas and literals are not credentials.
    [InlineData("if (password == null) return;")]
    [InlineData("tokens.Where(token => token.IsValid)")]
    [InlineData("requiresAuth: true")]
    [InlineData("password: null")]
    // Ordinary long values.
    [InlineData("path: src/Vellum/Modules/Ai/Providers/OpenAiCompatible/OpenAiCompatibleAdapter.cs")]
    [InlineData("image = registry.example.com/acme/orders-service:2026.10.13")]
    [InlineData("name = TheOrderWasCreatedAndTheInvoiceWasSentToTheCustomer")]
    [InlineData("see https://example.com/docs?contact=team@example.com for details")]
    [InlineData("the risk-assessment-of-the-new-design-document is attached")]
    public void Ordinary_text_stays_as_it_is(string text) => Assert.Equal(text, SecretRedactor.Redact(text));

    [Fact]
    public void The_text_around_a_credential_stays()
    {
        var redacted = SecretRedactor.Redact("line one\nDB_PASS=hunter2\nline three\n{\"apiKey\": \"k1\", \"region\": \"eu-west-1\"}\nlast");

        Assert.Equal($"line one\nDB_PASS={SecretRedactor.Marker}\nline three\n{{\"apiKey\": \"{SecretRedactor.Marker}\", \"region\": \"eu-west-1\"}}\nlast", redacted);
    }

    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----\n")]
    [InlineData("a=")]
    [InlineData("a:a=a:")]
    [InlineData("Bearer ")]
    [InlineData("x://")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n")]
    [InlineData("\"\\\"\\\"\\\"")]
    [InlineData("eyJ.")]
    [InlineData("password=")]
    public void Text_built_to_make_a_pattern_slow_is_still_read_in_linear_time(string unit)
    {
        // 2 MB of the unit. A pattern that backtracks, or a scan that looks back without a bound, takes minutes on some of these.
        var text = string.Concat(Enumerable.Repeat(unit, 2 * 1024 * 1024 / unit.Length));
        var watch = Stopwatch.StartNew();

        SecretRedactor.Redact(text);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed} for {unit.Trim()}");
    }
}
