using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class ListMessagesTool
{
    [McpServerTool(Name = "list_messages"), Description("List all messages in a project with producer and consumer element names resolved")]
    public static async Task<string> Execute(
        [Description("Project ID")] Guid projectId,
        ModellingDbContext db,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var proj = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (proj is null)
            return """{"error":"Project not found"}""";

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == proj.StreamId)
            .ToListAsync(ct);

        // Collect all element IDs we need to resolve
        var elementIds = messages
            .Select(m => m.ProducerId)
            .Concat(messages.SelectMany(m => m.ConsumerIds))
            .Distinct()
            .ToList();

        var elements = await db.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == proj.StreamId && elementIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Name, e.Kind })
            .ToListAsync(ct);

        var elementMap = elements.ToDictionary(e => e.Id);

        var enriched = messages.Select(m => new
        {
            m.Id,
            m.Name,
            m.Description,
            m.SchemaId,
            m.Tags,
            Producer = elementMap.TryGetValue(m.ProducerId, out var producer)
                ? new { producer.Id, producer.Name, producer.Kind }
                : new { Id = m.ProducerId, Name = (string)"(unknown)", Kind = (string)"" },
            Consumers = m.ConsumerIds
                .Select(cId => elementMap.TryGetValue(cId, out var consumer)
                    ? new { consumer.Id, consumer.Name, consumer.Kind }
                    : new { Id = cId, Name = (string)"(unknown)", Kind = (string)"" })
                .ToList()
        }).ToList();

        return JsonSerializer.Serialize(new { projectId, count = enriched.Count, messages = enriched });
    }
}
