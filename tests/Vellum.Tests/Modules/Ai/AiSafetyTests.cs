using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.GitHub;
using Vellum.Modules.Settings;
using Vellum.Modules.Settings.Entities;
using Vellum.Modules.Settings.Secrets;
using Vellum.Tests.Shared.Outbound;
using Vellum.Shared;

namespace Vellum.Tests.Modules.Ai;

/// <summary>
/// On a local instance: the gate for private code, settings and stored credentials that
/// cannot be sent, and what a failure tells the caller about a private server.
/// </summary>
public class AiSafetyTests(SharedOutboundHost shared) : IClassFixture<SharedOutboundHost>
{
    private readonly OutboundHost _host = shared.Host;

    private static AiFailure FailureOf(AiResult<Summary> result) => Assert.IsType<AiResult<Summary>.Failed>(result).Failure;

    private static AiPart PrivateCode => AiPart.Untrusted("internal class Billing { }", AiDataClass.PrivateRepository);

    // ---- Private code ----

    [Fact]
    public async Task By_default_a_local_workspace_sends_no_private_code_and_names_the_setting()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer)).Sse(ai.Fixture("stream.sse"));

        var classified = Assert.IsType<AiFailure.Disabled>(FailureOf(await ai.StructuredAsync(ai.Request(AiPart.Instruction("Summarise."), PrivateCode))));
        // A part that states no class is the restrictive class.
        var unclassified = Assert.IsType<AiFailure.Disabled>(FailureOf(await ai.StructuredAsync(
            ai.Request(new AiPart(AiPartKind.Untrusted, "internal class Billing { }")))));

        using var scope = _host.Factory.Services.CreateScope();
        var streamed = await Assert.ThrowsAsync<AiStreamFailedException>(async () =>
        {
            await foreach (var _ in scope.ServiceProvider.GetRequiredService<AiClient>().StreamTextAsync(ai.Request(PrivateCode)))
            {
            }
        });

        Assert.Equal("privacy.sendPrivateCodeToAi", classified.Setting);
        Assert.Equal("privacy.sendPrivateCodeToAi", unclassified.Setting);
        Assert.Equal("privacy.sendPrivateCodeToAi", Assert.IsType<AiFailure.Disabled>(streamed.Failure).Setting);
        Assert.Empty(ai.Stub.Requests);
        // Nothing was admitted or metered.
        Assert.Empty(await ai.RowsAsync());
    }

    [Fact]
    public async Task Public_text_and_instructions_need_no_setting()
    {
        var ai = await AiScenario.CreateAsync(_host, "anthropic");
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));

        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync(
            ai.Request(AiPart.Instruction("Summarise."), AiPart.Untrusted("PUBLIC-README-TEXT", AiDataClass.Public))));

        Assert.Contains("PUBLIC-README-TEXT", Assert.Single(ai.Stub.Requests).Body);
    }

    [Fact]
    public async Task A_local_workspace_that_switches_the_setting_on_sends_private_code()
    {
        var ai = await AiScenario.CreateAsync(_host, "gemini");
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));
        var settings = (await ai.Owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{ai.WorkspaceId}/settings"))!;
        var overrides = settings["overrides"]!.DeepClone().AsObject();
        overrides["privacy"] = new JsonObject { ["sendPrivateCodeToAi"] = true };
        await OutboundHost.PutSettingsAsync(ai.Owner, ai.WorkspaceId, overrides);

        Assert.IsType<AiResult<Summary>.Succeeded>(await ai.StructuredAsync(ai.Request(AiPart.Instruction("Summarise."), PrivateCode)));

        Assert.Contains("internal class Billing", Assert.Single(ai.Stub.Requests).Body);
    }

    // ---- Values that cannot be sent ----

    [Theory]
    [InlineData("openai", "gpt-x\r\nX-Evil: 1")]
    [InlineData("anthropic", "claude-x\nX-Evil: 1")]
    [InlineData("gemini", "gemini-x\r\nX-Evil: 1")]
    [InlineData("gemini", "gemini-x\0")]
    [InlineData("openai-compatible", "llama\u0085")]
    public async Task A_model_name_with_a_control_character_is_a_typed_setting_failure_and_nothing_is_sent(string kind, string model)
    {
        // The settings refuse such a name when it is saved. The adapters are called directly
        // here, as with a name that was stored before that rule or that came from another source.
        var host = $"{Guid.NewGuid():N}.ai.test";
        var stub = _host.Transport.For(host);
        stub.Json("{}");
        using var scope = _host.Factory.Services.CreateScope();
        var adapter = scope.ServiceProvider.GetServices<IAiProviderAdapter>().Single(a => a.Kind == kind);
        var progress = new CallProgress();
        var request = new ProviderRequest(
            new ProviderTarget(Guid.NewGuid(), kind, model, new Uri($"https://{host}/v1"), adapter.Credential(AiScenario.ApiKey),
                AllowPrivateNetwork: false, RequireHttps: true, TimeSpan.FromSeconds(30), Temperature: null),
            "System.", [new ProviderMessage(AiRole.User, "Go.")], null, "result", 256, progress);

        var whole = await adapter.CompleteAsync(request, default);
        ProviderReply? streamed = null;
        await foreach (var item in adapter.StreamAsync(request, default))
            streamed = (item as ProviderStreamEvent.End)?.Reply ?? streamed;

        Assert.Equal("invalid_setting", whole.Error?.Code);
        Assert.Contains("model name", whole.Error!.Message);
        Assert.Equal("invalid_setting", streamed?.Error?.Code);
        Assert.DoesNotContain("X-Evil", whole.Error.Message);
        Assert.Empty(stub.Requests);
        // Nothing was sent, so the provider generated nothing that it bills.
        Assert.False(progress.MayHaveGenerated(whole.Error));
    }

    /// <summary>Writes a credential straight into the store, as a row from before the save rule existed.</summary>
    private async Task StoreUncheckedAsync(Guid workspaceId, string name, string origin, string value)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<Bootstrap>().Keys;
        var row = new SecretEntity { WorkspaceId = workspaceId, Name = name, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" };
        var bytes = Encoding.UTF8.GetBytes(value);
        SecretEnvelope.Seal(keys, row, origin, bytes);
        CryptographicOperations.ZeroMemory(bytes);
        db.Secrets.Add(row);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_stored_credential_that_would_add_a_header_is_refused_at_send_time_with_a_typed_failure()
    {
        const string injected = "tok\r\nX-Evil: 1";
        var ai = await AiScenario.CreateAsync(_host, "anthropic", withKey: false);
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));
        await StoreUncheckedAsync(ai.WorkspaceId, $"ai:{ai.ProviderId:D}:apiKey", $"https://{ai.HostName}", injected);

        var githubHost = $"{Guid.NewGuid():N}.gh.test";
        await OutboundHost.PutSettingsAsync(ai.Owner, ai.WorkspaceId, new JsonObject
        {
            ["ai"] = (await ai.Owner.GetFromJsonAsync<JsonObject>($"/api/workspaces/{ai.WorkspaceId}/settings"))!["overrides"]!["ai"]!.DeepClone(),
            ["github"] = new JsonObject { ["apiBaseUrl"] = $"https://{githubHost}" },
        });
        _host.Transport.For(githubHost).Json(Fixtures.Read("GitHub/user.json"));
        await StoreUncheckedAsync(ai.WorkspaceId, "github:token", $"https://{githubHost}", injected);

        var aiFailure = Assert.IsType<AiFailure.ProviderError>(FailureOf(await ai.StructuredAsync()));
        using var scope = _host.Factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<GitHubReader>().ConnectAsync(ai.WorkspaceId);
        var read = await connection.Value!.GetRepositoryAsync("acme", "orders");
        var test = (await (await ai.Owner.PostAsync($"/api/workspaces/{ai.WorkspaceId}/github/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;

        // Typed, with a sentence that says what to do. Never the word for a network fault.
        Assert.Equal("invalid_setting", aiFailure.Code);
        Assert.Contains("Save the API key again", aiFailure.Message);
        Assert.Equal("invalid_setting", Assert.IsType<GitHubFailure.Unavailable>(read.Failure).Reason);
        Assert.Equal("invalid_setting", test["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Save the token again", test["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("X-Evil", aiFailure.Message + test.ToJsonString());
        Assert.Empty(ai.Stub.Requests);
        Assert.Empty(_host.Transport.For(githubHost).Requests);
    }

    // ---- What a failure says about a private server ----

    [Fact]
    public async Task The_connection_test_does_not_repeat_the_text_of_a_server_on_a_private_network()
    {
        const string internalText = "INTERNAL-SERVICE-ANSWER admin panel v2";
        var body = new JsonObject { ["error"] = new JsonObject { ["message"] = internalText, ["type"] = "invalid_request_error" } }.ToJsonString();
        var onPrivate = await AiScenario.CreateAsync(_host, "openai-compatible");
        _host.Addresses[onPrivate.HostName] = "10.0.0.5";
        onPrivate.Stub.Json(HttpStatusCode.BadRequest, body);
        var onPublic = await AiScenario.CreateAsync(_host, "openai-compatible");
        onPublic.Stub.Json(HttpStatusCode.BadRequest, body);

        async Task<string> TestAsync(AiScenario ai) =>
            (await (await ai.Owner.PostAsync($"/api/workspaces/{ai.WorkspaceId}/ai/providers/{ai.ProviderId}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!["error"]!.ToJsonString();

        var privateError = await TestAsync(onPrivate);
        var publicError = await TestAsync(onPublic);
        var privateCall = Assert.IsType<AiFailure.ProviderError>(FailureOf(await onPrivate.StructuredAsync()));

        Assert.Contains("invalid_request", privateError);
        Assert.Contains("The provider rejected the request", privateError);
        Assert.DoesNotContain("INTERNAL-SERVICE", privateError);
        Assert.DoesNotContain("INTERNAL-SERVICE", privateCall.Message);
        // A public server's sentence helps the user and is kept, cut and redacted.
        Assert.Contains(internalText, publicError);
    }

    // ---- Vendor rules for a request ----

    [Fact]
    public async Task Anthropic_gets_no_temperature_and_no_thinking_field_unless_set_and_room_for_thinking()
    {
        var plain = await AiScenario.CreateAsync(_host, "anthropic");
        plain.Stub.Json(plain.Completion(AiScenario.ValidAnswer));
        var withTemperature = await AiScenario.CreateAsync(_host, "anthropic", p => p["temperature"] = 0.2);
        withTemperature.Stub.Json(HttpStatusCode.BadRequest,
            """{"type":"error","error":{"type":"invalid_request_error","message":"`temperature` is not supported for this model."}}""");

        Assert.IsType<AiResult<Summary>.Succeeded>(await plain.StructuredAsync());
        var rejected = Assert.IsType<AiFailure.ProviderError>(FailureOf(await withTemperature.StructuredAsync()));
        var test = (await (await withTemperature.Owner.PostAsync(
            $"/api/workspaces/{withTemperature.WorkspaceId}/ai/providers/{withTemperature.ProviderId}/test", null)).Content.ReadFromJsonAsync<JsonObject>())!;

        var body = JsonNode.Parse(Assert.Single(plain.Stub.Requests).Body)!.AsObject();
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("thinking"));
        // max_tokens caps thinking and answer together, so the output limit of 256 gets the headroom.
        Assert.Equal(256 + 8192, body["max_tokens"]!.GetValue<int>());

        Assert.Equal("unsupported_parameter", rejected.Code);
        Assert.Contains("Temperature is optional", rejected.Message);
        Assert.Equal("unsupported_parameter", test["error"]!["code"]!.GetValue<string>());
        Assert.Contains("clear the temperature", test["error"]!["message"]!.GetValue<string>());
        // One request for the call and one for the test: the rejection is not tried again as plain text.
        Assert.Equal(2, withTemperature.Stub.Requests.Count);
    }

    [Fact]
    public async Task Openai_refuses_an_output_limit_below_the_minimum_of_the_responses_api_before_it_sends()
    {
        var ai = await AiScenario.CreateAsync(_host, "openai");
        ai.Stub.Json(ai.Completion(AiScenario.ValidAnswer));

        var tooSmall = Assert.IsType<AiFailure.ProviderError>(FailureOf(await ai.StructuredAsync(ai.Request() with { MaxOutputTokens = 15 })));
        var atMinimum = await ai.StructuredAsync(ai.Request() with { MaxOutputTokens = 16 });

        Assert.Equal("output_limit_too_small", tooSmall.Code);
        Assert.IsType<AiResult<Summary>.Succeeded>(atMinimum);
        Assert.Contains("\"max_output_tokens\":16", Assert.Single(ai.Stub.Requests).Body);
    }
}
