using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Vellum.Modules.Scoring;

public static class ScoringModule
{
    public static IServiceCollection AddScoringModule(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")!;

        services.AddDbContext<ScoringDbContext>(options =>
            options.UseNpgsql(connectionString)
                   .UseSnakeCaseNamingConvention());

        services.AddSingleton<RubricService>();

        var aiSection = config.GetSection("AI");
        var apiKey = aiSection["ApiKey"];

        if (!string.IsNullOrEmpty(apiKey))
        {
            var model = aiSection["Model"] ?? "gpt-4o";
            var endpoint = aiSection["Endpoint"];

            var credential = new ApiKeyCredential(apiKey);
            var clientOptions = new OpenAIClientOptions();
            if (endpoint is not null)
                clientOptions.Endpoint = new Uri(endpoint);

            var chatClient = new ChatClient(model, credential, clientOptions);
            services.AddSingleton<IChatClient>(chatClient.AsIChatClient());
        }

        return services;
    }
}
