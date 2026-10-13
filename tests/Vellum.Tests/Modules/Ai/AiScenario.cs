using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Usage;
using Vellum.Tests.Shared.Outbound;

namespace Vellum.Tests.Modules.Ai;

public sealed record Summary(string Title, int Count);

/// <summary>One workspace with one provider entry that every task is routed to, and that provider's stub transport.</summary>
internal sealed class AiScenario
{
    public const string ApiKey = "sk-test-4f9a1c7e2b8d4a6f9c0e1b2d3a4f5c6e";
    public const string ValidAnswer = """{"title":"Orders","count":2}""";
    public static readonly string[] Kinds = ["openai", "anthropic", "gemini", "openai-compatible"];

    public static readonly JsonElement SummarySchema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string" },
            ["count"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray("title", "count"),
        ["additionalProperties"] = false,
    });

    private readonly OutboundHost _host;

    private AiScenario(OutboundHost host, HttpClient owner, Guid workspaceId, Guid providerId, string kind, string model, string hostName)
    {
        _host = host;
        Owner = owner;
        WorkspaceId = workspaceId;
        ProviderId = providerId;
        Kind = kind;
        Model = model;
        HostName = hostName;
    }

    public HttpClient Owner { get; }
    public Guid WorkspaceId { get; }
    public Guid ProviderId { get; }
    public string Kind { get; }
    public string Model { get; }
    public string HostName { get; }
    public StubTransport Stub => _host.Transport.For(HostName);

    /// <param name="provider">Changes the provider entry before it is saved.</param>
    /// <param name="usage">Sets <c>usage</c> of the workspace settings.</param>
    public static async Task<AiScenario> CreateAsync(
        OutboundHost host, string kind, Action<JsonObject>? provider = null, bool withKey = true, bool route = true,
        JsonObject? usage = null, bool defaultBaseUrl = false)
    {
        var (owner, workspaceId) = await host.NewWorkspaceAsync();
        var providerId = Guid.NewGuid();
        var hostName = $"{Guid.NewGuid():N}.ai.test";
        var model = $"model-{Guid.NewGuid():N}";
        var entry = new JsonObject
        {
            ["id"] = providerId,
            ["kind"] = kind,
            ["name"] = $"Test {kind}",
            ["model"] = model,
            ["baseUrl"] = defaultBaseUrl ? "" : kind is "openai" or "openai-compatible" ? $"https://{hostName}/v1" : $"https://{hostName}",
        };
        provider?.Invoke(entry);

        var id = route ? providerId : (Guid?)null;
        var overrides = new JsonObject
        {
            ["ai"] = new JsonObject
            {
                ["providers"] = new JsonArray(entry),
                ["taskRouting"] = new JsonObject { ["inferArchitecture"] = id, ["proposeFlow"] = id, ["critique"] = id, ["explain"] = id },
            },
        };
        if (usage is not null)
            overrides["usage"] = usage;

        await OutboundHost.PutSettingsAsync(owner, workspaceId, overrides);
        if (withKey)
            await OutboundHost.PutSecretAsync(owner, workspaceId, $"ai:{providerId:D}:apiKey", ApiKey);

        return new AiScenario(host, owner, workspaceId, providerId, kind, entry["model"]!.GetValue<string>(), hostName);
    }

    public string Fixture(string name) => Fixtures.Read($"Ai/{Kind}/{name}");

    public string Completion(string text) => Fixtures.WithText($"Ai/{Kind}/completion.json", text);

    public AiRequest Request(params AiPart[] parts) => new(
        WorkspaceId, AiTask.ProposeFlow, "summarise", "summarise/1", "You summarise a repository.",
        [new AiMessage(AiRole.User, parts.Length > 0 ? parts : [AiPart.Instruction("Summarise the repository.")])],
        SummarySchema, MaxOutputTokens: 256, ActorId: "user:test");

    public async Task<AiResult<Summary>> StructuredAsync(AiRequest? request = null, CancellationToken ct = default)
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiClient>().CompleteStructuredAsync<Summary>(request ?? Request(), ct);
    }

    /// <summary>Reads the stream to its end. Returns the chunks and the failure that ended it, if any.</summary>
    public async Task<(List<string> Chunks, AiFailure? Failure)> StreamAsync(CancellationToken ct = default)
    {
        using var scope = _host.Factory.Services.CreateScope();
        var chunks = new List<string>();
        try
        {
            await foreach (var chunk in scope.ServiceProvider.GetRequiredService<AiClient>().StreamTextAsync(Request(), ct))
                chunks.Add(chunk);
            return (chunks, null);
        }
        catch (AiStreamFailedException ex)
        {
            return (chunks, ex.Failure);
        }
    }

    public async Task<List<UsageRecordEntity>> RowsAsync()
    {
        using var scope = _host.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiUsageDbContext>().UsageRecords.AsNoTracking()
            .Where(r => r.WorkspaceId == WorkspaceId)
            .OrderBy(r => r.StartedAt)
            .ToListAsync();
    }

    /// <summary>The path a request of this kind goes to.</summary>
    public string ExpectedPath(bool stream = false) => Kind switch
    {
        "openai" => "/v1/responses",
        "anthropic" => "/v1/messages",
        "gemini" => $"/v1beta/models/{Model}:{(stream ? "streamGenerateContent" : "generateContent")}",
        _ => "/v1/chat/completions",
    };

    /// <summary>The header that carries the API key for this kind, with the value it must have.</summary>
    public (string Name, string Value) ExpectedCredential => Kind switch
    {
        "anthropic" => ("x-api-key", ApiKey),
        "gemini" => ("x-goog-api-key", ApiKey),
        _ => ("Authorization", $"Bearer {ApiKey}"),
    };

    /// <summary>The field that holds the schema when the provider's own structured-output mode is used.</summary>
    public string NativeSchemaField => Kind switch
    {
        "openai" => "\"text\":{\"format\"",
        "anthropic" => "\"output_config\"",
        "gemini" => "\"responseFormat\"",
        _ => "\"response_format\"",
    };
}
