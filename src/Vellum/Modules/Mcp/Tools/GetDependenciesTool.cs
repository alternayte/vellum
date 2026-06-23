using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class GetDependenciesTool
{
    [McpServerTool(Name = "get_dependencies"), Description("Find what depends on element X — inbound relationships and messages where X is a consumer")]
    public static async Task<string> Execute(
        [Description("Project ID")] Guid projectId,
        [Description("Element ID")] Guid elementId,
        ModellingDbContext db,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var proj = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (proj is null)
            return """{"error":"Project not found"}""";

        var inbound = await db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == proj.StreamId && r.ToId == elementId)
            .Select(r => new { r.Id, r.FromId, r.Label, r.Technology })
            .ToListAsync(ct);

        var consumedMessages = await db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == proj.StreamId && m.ConsumerIds.Contains(elementId))
            .Select(m => new { m.Id, m.Name, m.ProducerId, m.Tags })
            .ToListAsync(ct);

        var result = new
        {
            elementId,
            projectId,
            inboundRelationships = inbound,
            consumedMessages
        };

        return JsonSerializer.Serialize(result);
    }
}
