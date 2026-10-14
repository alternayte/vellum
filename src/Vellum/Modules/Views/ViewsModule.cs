using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Views;

public static class ViewsModule
{
    public static IServiceCollection AddViewsModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, ViewsRetention>();

        return services;
    }
}

/// <summary>What the view tables keep of a deleted project: nothing.</summary>
public sealed class ViewsRetention(AppDbContext db) : IProjectRetention
{
    // The layout positions and edges of a view go with its row (ON DELETE CASCADE).
    public Task DeleteProjectAsync(DeletedProject project, CancellationToken ct) =>
        db.Views.Where(v => v.ProjectId == project.Id).ExecuteDeleteAsync(ct);
}
