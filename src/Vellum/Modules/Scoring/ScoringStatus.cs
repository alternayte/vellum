using Microsoft.Extensions.AI;

namespace Vellum.Modules.Scoring;

public static class ScoringStatus
{
    public static IResult Handle(IServiceProvider services)
    {
        var chatClient = services.GetService<IChatClient>();
        return Results.Ok(new { available = chatClient is not null });
    }
}
