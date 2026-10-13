using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.Ai.Providers.Anthropic;
using Vellum.Modules.Ai.Providers.Gemini;
using Vellum.Modules.Ai.Providers.OpenAi;
using Vellum.Modules.Ai.Providers.OpenAiCompatible;
using Vellum.Modules.GitHub;
using Vellum.Shared;
using Vellum.Shared.Outbound;
using Vellum.Tests.Modules.Ai;

namespace Vellum.Tests;

/// <summary>A test that calls a real service. It is skipped, and reported as skipped, unless its variable is set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = $"Live smoke test. Set {variable} to run it.";
    }
}

/// <summary>
/// One real structured call and one real stream per provider, and one real GitHub read,
/// through the production pipeline and transport. Each runs only when its variable is set:
/// <c>VELLUM_LIVE_OPENAI_KEY</c>, <c>VELLUM_LIVE_ANTHROPIC_KEY</c>, <c>VELLUM_LIVE_GEMINI_KEY</c>,
/// <c>VELLUM_LIVE_OLLAMA_URL</c>, <c>VELLUM_LIVE_GITHUB_TOKEN</c>. The model comes from
/// <c>VELLUM_LIVE_OPENAI_MODEL</c>, <c>..._ANTHROPIC_MODEL</c>, <c>..._GEMINI_MODEL</c> and
/// <c>..._OLLAMA_MODEL</c>; the names below are used when those are not set.
/// </summary>
public class LiveSmokeTests
{
    private static string Variable(string name, string fallback = "") =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HostResolver>((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddOutbound();
        services.AddOutboundClient<IGitHubApi>();
        services.AddOutboundClient<IOpenAiApi>();
        services.AddOutboundClient<IAnthropicApi>();
        services.AddOutboundClient<IGeminiApi>();
        services.AddOutboundClient<IOpenAiCompatibleApi>();
        services.AddTransient<IAiProviderAdapter, OpenAiAdapter>();
        services.AddTransient<IAiProviderAdapter, AnthropicAdapter>();
        services.AddTransient<IAiProviderAdapter, GeminiAdapter>();
        services.AddTransient<IAiProviderAdapter, OpenAiCompatibleAdapter>();
        return services.BuildServiceProvider();
    }

    private static async Task CallAndStreamAsync(string kind, string model, string? apiKey, string? baseUrl, bool allowPrivateNetwork)
    {
        await using var provider = Build();
        var adapter = provider.GetServices<IAiProviderAdapter>().Single(a => a.Kind == kind);
        var target = new ProviderTarget(
            Guid.NewGuid(), kind, model, baseUrl is null ? adapter.DefaultBaseUrl! : new Uri(baseUrl),
            apiKey is null ? null : adapter.Credential(apiKey), allowPrivateNetwork, RequireHttps: !allowPrivateNetwork, TimeSpan.FromSeconds(120), Temperature: null);

        var structured = await adapter.CompleteAsync(new ProviderRequest(
            target, "You answer with JSON only.",
            [new ProviderMessage(AiRole.User, """Reply with the JSON object {"title":"Orders","count":2}.""")],
            AiScenario.SummarySchema, "summary", MaxOutputTokens: 2048, new CallProgress()), default);

        Assert.True(structured.Error is null, $"{structured.Error?.Code}: {structured.Error?.Message}");
        Assert.Equal(ProviderStop.Completed, structured.Stop);
        Assert.Empty(StructuredOutput.Compile(AiScenario.SummarySchema)!.Check<Summary>(structured.Text, lenient: false, out var summary));
        Assert.Equal(new Summary("Orders", 2), summary);
        Assert.NotNull(structured.ReportedModel);

        var text = "";
        ProviderReply? end = null;
        await foreach (var item in adapter.StreamAsync(new ProviderRequest(
            target, "You answer briefly.", [new ProviderMessage(AiRole.User, "Say the word hello.")], null, "text", MaxOutputTokens: 2048, new CallProgress()), default))
        {
            if (item is ProviderStreamEvent.Delta delta)
                text += delta.Text;
            else if (item is ProviderStreamEvent.End last)
                end = last.Reply;
        }

        Assert.NotNull(end);
        Assert.True(end.Error is null, $"{end.Error?.Code}: {end.Error?.Message}");
        Assert.Contains("hello", text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact("VELLUM_LIVE_OPENAI_KEY")]
    public Task OpenAi_answers_a_structured_call_and_a_stream() => CallAndStreamAsync(
        "openai", Variable("VELLUM_LIVE_OPENAI_MODEL", "gpt-6-astra"), Variable("VELLUM_LIVE_OPENAI_KEY"), null, allowPrivateNetwork: false);

    [LiveFact("VELLUM_LIVE_ANTHROPIC_KEY")]
    public Task Anthropic_answers_a_structured_call_and_a_stream() => CallAndStreamAsync(
        "anthropic", Variable("VELLUM_LIVE_ANTHROPIC_MODEL", "claude-opus-5-5"), Variable("VELLUM_LIVE_ANTHROPIC_KEY"), null, allowPrivateNetwork: false);

    [LiveFact("VELLUM_LIVE_GEMINI_KEY")]
    public Task Gemini_answers_a_structured_call_and_a_stream() => CallAndStreamAsync(
        "gemini", Variable("VELLUM_LIVE_GEMINI_MODEL", "gemini-3.8-flash"), Variable("VELLUM_LIVE_GEMINI_KEY"), null, allowPrivateNetwork: false);

    /// <summary><c>VELLUM_LIVE_OLLAMA_URL</c> is the OpenAI-compatible base, for example <c>http://localhost:11434/v1</c>.</summary>
    [LiveFact("VELLUM_LIVE_OLLAMA_URL")]
    public Task An_openai_compatible_server_answers_a_structured_call_and_a_stream() => CallAndStreamAsync(
        "openai-compatible", Variable("VELLUM_LIVE_OLLAMA_MODEL", "llama3.2"), null, Variable("VELLUM_LIVE_OLLAMA_URL"), allowPrivateNetwork: true);

    [LiveFact("VELLUM_LIVE_GITHUB_TOKEN")]
    public async Task GitHub_identifies_the_token_and_reads_a_public_repository()
    {
        await using var provider = Build();
        var connection = new GitHubConnection(
            provider.GetRequiredService<IGitHubApi>(), new Uri("https://api.github.com"), allowPrivateNetwork: false, requireHttps: true,
            new OutboundCredential("Authorization", "Bearer", Variable("VELLUM_LIVE_GITHUB_TOKEN")));

        var identity = await connection.IdentifyAsync();
        Assert.True(identity.Ok, identity.Failure?.ToString());
        Assert.NotNull(identity.Value!.Login);

        var repository = await connection.GetRepositoryAsync("octocat", "Hello-World");
        Assert.True(repository.Ok, repository.Failure?.ToString());
        var sha = await connection.ResolveRefAsync("octocat", "Hello-World", repository.Value!.DefaultBranch);
        Assert.True(sha.Ok, sha.Failure?.ToString());
        var tree = await connection.GetTreeAsync("octocat", "Hello-World", sha.Value!);
        Assert.True(tree.Ok, tree.Failure?.ToString());
        Assert.Contains(tree.Value!.Entries, entry => entry.Path == "README");
    }
}
