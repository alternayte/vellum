using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Comments;

/// <summary>
/// Comment threads on the targets of a project, the inbox of each person, and the webhook
/// events of a workspace. Comments live outside the model streams: a comment is an opinion,
/// never a fact. No export, SDD or agent brief reads these tables.
/// </summary>
public static class CommentsModule
{
    /// <param name="runWorker">False when the host is started only to write the OpenAPI document.</param>
    public static IServiceCollection AddCommentsModule(this IServiceCollection services, bool runWorker)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOutboundClient<ICommentWebhookApi>();
        services.AddSingleton<WebhookSignal>();
        services.AddSingleton<WebhookSender>();
        services.AddScoped<IProjectRetention, CommentsRetention>();
        if (runWorker)
            services.AddHostedService<WebhookWorkerService>();
        return services;
    }

    public static WebApplication MapCommentEndpoints(this WebApplication app)
    {
        var threads = app.MapGroup("/api/projects/{projectId}/threads")
            .RequireAuthorization()
            .WithTags("Comments");

        threads.MapGet("/", ThreadEndpoints.List).Produces<Page<ThreadDto>>();
        threads.MapPost("/", ThreadEndpoints.Create).Produces<ThreadDto>(StatusCodes.Status201Created).Produces<ThreadDto>();
        threads.MapGet("/{threadId}", ThreadEndpoints.Get).Produces<ThreadDto>();
        threads.MapPost("/{threadId}/replies", ThreadEndpoints.Reply).Produces<CommentDto>(StatusCodes.Status201Created).Produces<CommentDto>();
        threads.MapPatch("/{threadId}/comments/{commentId}", ThreadEndpoints.Edit).Produces<CommentDto>();
        threads.MapDelete("/{threadId}/comments/{commentId}", ThreadEndpoints.Delete).Produces(StatusCodes.Status204NoContent);
        threads.MapPost("/{threadId}/resolve", ThreadEndpoints.Resolve).Produces<ThreadDto>();
        threads.MapPost("/{threadId}/reopen", ThreadEndpoints.Reopen).Produces<ThreadDto>();
        threads.MapPost("/{threadId}/raise-question", RaiseEndpoints.AsQuestion)
            .Produces<Review.QuestionDto>(StatusCodes.Status201Created).Produces<Review.QuestionDto>();
        threads.MapPost("/{threadId}/raise-decision", RaiseEndpoints.AsDecision)
            .Produces<Review.DecisionDto>(StatusCodes.Status201Created);

        var inbox = app.MapGroup("/api/inbox")
            .RequireAuthorization()
            .WithTags("Inbox");
        inbox.MapGet("/", InboxEndpoints.List).Produces<InboxResponse>();
        inbox.MapPost("/{itemId}/read", InboxEndpoints.MarkRead).Produces(StatusCodes.Status204NoContent);
        inbox.MapPost("/read-all", InboxEndpoints.MarkAllRead).Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

/// <summary>What the comment tables keep of a deleted project: nothing. Comments and inbox entries go with their threads.</summary>
public sealed class CommentsRetention(AppDbContext db) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        await db.InboxItems.Where(i => i.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.WebhookDeliveries.Where(d => d.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        // The comments of a thread go with its row (ON DELETE CASCADE).
        await db.CommentThreads.Where(t => t.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}

/// <summary>What a draft merge does to the threads of the draft.</summary>
public static class DraftThreads
{
    /// <summary>
    /// Moves the open threads of a merged draft to main, with their targets. Called in the
    /// merge transaction. Resolved and detached threads stay with the draft as its history.
    /// </summary>
    public static Task<int> MoveToMainAsync(AppDbContext db, Guid draftId, CancellationToken ct) =>
        db.CommentThreads
            .Where(t => t.DraftId == draftId && t.Status == ThreadStatus.Open && !t.Detached)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.DraftId, (Guid?)null), ct);
}
