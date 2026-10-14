using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Settings;

/// <summary>Which characters a stored credential can hold. A credential that travels in a header must be one header value.</summary>
public class SecretValueTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    [Theory]
    [InlineData("tok\r\nX-Evil: 1")]
    [InlineData("tok\nX-Evil: 1")]
    [InlineData("tok\rX")]
    [InlineData("tok\0")]
    [InlineData("tok\ten")]
    [InlineData("tok\u007f")]
    [InlineData("tok\u0085")]
    [InlineData("tök")]
    [InlineData("tok\u2028en")]
    [InlineData("токен")]
    public async Task A_header_credential_with_a_control_or_non_ascii_character_is_refused_and_not_stored(string value)
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        var providerId = Guid.NewGuid();
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject
        {
            ["ai"] = new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject { ["id"] = providerId, ["kind"] = "openai", ["name"] = "n", ["model"] = "m" }),
            },
        });

        foreach (var name in new[] { "github:token", $"ai:{providerId:D}:apiKey" })
        {
            var response = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/{name}", new { value });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("value", error["errors"]![0]!["field"]!.GetValue<string>());
            Assert.DoesNotContain("X-Evil", error.ToJsonString());
        }

        var listed = (await owner.GetFromJsonAsync<JsonArray>($"/api/workspaces/{workspaceId}/secrets"))!;
        Assert.All(listed, secret => Assert.False(secret!["isSet"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_printable_ascii_header_credential_is_stored()
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();

        var response = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/github:token",
            new { value = "ghp_AZaz09 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("pässwörd with spaces, 秘密 and ☃", HttpStatusCode.NoContent)]
    [InlineData("whsec_AbCd/+=", HttpStatusCode.NoContent)]
    [InlineData("line\nbreak", HttpStatusCode.BadRequest)]
    [InlineData("nul\0", HttpStatusCode.BadRequest)]
    [InlineData("tab\tbed", HttpStatusCode.BadRequest)]
    [InlineData("c1\u0085", HttpStatusCode.BadRequest)]
    public async Task A_webhook_signing_secret_is_any_printable_text_without_control_characters(string value, HttpStatusCode expected)
    {
        var (owner, workspaceId) = await _host.NewWorkspaceAsync();
        await OutboundHost.PutSettingsAsync(owner, workspaceId, new JsonObject
        {
            ["notifications"] = new JsonObject { ["webhookUrl"] = "https://hooks.example.test/in" },
        });

        var response = await owner.PutAsJsonAsync($"/api/workspaces/{workspaceId}/secrets/notifications:webhookSecret", new { value });

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
            Assert.Equal("value", (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["field"]!.GetValue<string>());
    }
}
