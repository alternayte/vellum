using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class FindElementsTool
{
    [McpServerTool(Name = "find_elements"), Description("Search elements by name, kind, tag, or status within the main branch of a project")]
    public static async Task<string> Execute(
        [Description("Project ID")] Guid projectId,
        [Description("Optional name substring to filter by (case-insensitive)")] string? name,
        [Description("Optional element kind to filter by (e.g. Actor, System, App, Component, Store)")] string? kind,
        [Description("Optional tag to filter by")] string? tag,
        [Description("Optional status to filter by (e.g. current, planned, deprecated)")] string? status,
        ModellingDbContext db,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var proj = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (proj is null)
            return """{"error":"Project not found"}""";

        var query = db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == proj.StreamId);

        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(e => e.Name.ToLower().Contains(name.ToLower()));

        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(e => e.Kind == kind);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status == status);

        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(e => e.Tags.Contains(tag));

        var elements = await query
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Kind,
                e.Description,
                e.Technology,
                e.Status,
                e.ParentId,
                e.Tags
            })
            .OrderBy(e => e.Name)
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { projectId, count = elements.Count, elements });
    }
}
