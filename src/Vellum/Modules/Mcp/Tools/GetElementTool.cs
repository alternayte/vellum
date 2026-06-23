using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class GetElementTool
{
    [McpServerTool(Name = "get_element"), Description("Get full detail for one element including its relationships and messages")]
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

        var element = await db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == proj.StreamId && e.Id == elementId)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Kind,
                e.Description,
                e.Technology,
                e.OwnerId,
                e.Status,
                e.ParentId,
                e.Tags
            })
            .FirstOrDefaultAsync(ct);

        if (element is null)
            return """{"error":"Element not found"}""";

        var outboundRelationships = await db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == proj.StreamId && r.FromId == elementId)
            .Select(r => new { r.Id, r.ToId, r.Label, r.Technology, r.MessageId })
            .ToListAsync(ct);

        var inboundRelationships = await db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == proj.StreamId && r.ToId == elementId)
            .Select(r => new { r.Id, r.FromId, r.Label, r.Technology, r.MessageId })
            .ToListAsync(ct);

        var producedMessages = await db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == proj.StreamId && m.ProducerId == elementId)
            .Select(m => new { m.Id, m.Name, m.Description, m.ConsumerIds, m.SchemaId, m.Tags })
            .ToListAsync(ct);

        var consumedMessages = await db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == proj.StreamId && m.ConsumerIds.Contains(elementId))
            .Select(m => new { m.Id, m.Name, m.Description, m.ProducerId, m.SchemaId, m.Tags })
            .ToListAsync(ct);

        var result = new
        {
            element,
            outboundRelationships,
            inboundRelationships,
            producedMessages,
            consumedMessages
        };

        return JsonSerializer.Serialize(result);
    }
}
