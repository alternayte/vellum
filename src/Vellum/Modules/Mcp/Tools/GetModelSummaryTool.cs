using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class GetModelSummaryTool
{
    [McpServerTool(Name = "get_model_summary"), Description("Get a summary of the C4 model for a project: element counts by kind, and relationship/message/schema counts")]
    public static async Task<string> Execute(
        [Description("Project ID")] Guid projectId,
        ModellingDbContext db,
        WorkspacesDbContext workspacesDb,
        SchemasDbContext schemasDb,
        CancellationToken ct)
    {
        var proj = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (proj is null)
            return """{"error":"Project not found"}""";

        var elementsByKind = await db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == proj.StreamId)
            .GroupBy(e => e.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var relationshipCount = await db.Relationships.AsNoTracking()
            .CountAsync(r => r.ProjectId == projectId && r.Branch == proj.StreamId, ct);

        var messageCount = await db.Messages.AsNoTracking()
            .CountAsync(m => m.ProjectId == projectId && m.Branch == proj.StreamId, ct);

        var schemaCount = await schemasDb.Schemas.AsNoTracking()
            .CountAsync(s => s.ProjectId == projectId, ct);

        var totalElements = elementsByKind.Sum(e => e.Count);

        var result = new
        {
            projectId,
            projectName = proj.Name,
            totalElements,
            elementsByKind,
            relationshipCount,
            messageCount,
            schemaCount
        };

        return JsonSerializer.Serialize(result);
    }
}
