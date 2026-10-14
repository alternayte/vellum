using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class DocsModule
{
    public static IServiceCollection AddDocsModule(this IServiceCollection services)
    {
        services.AddScoped<Shared.IProjectRetention, DocsRetention>();

        return services;
    }
}

/// <summary>What the doc tables keep of a deleted project: nothing.</summary>
public sealed class DocsRetention(AppDbContext db) : IProjectRetention
{
    public async Task DeleteProjectAsync(DeletedProject project, CancellationToken ct)
    {
        await db.Documents.Where(d => d.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        await db.Spaces.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
    }
}
