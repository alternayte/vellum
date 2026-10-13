using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Mcp.Tools;

[McpServerToolType]
public class GetDependentsTool
{
    [McpServerTool(Name = "get_dependents"), Description("Find what element X depends on — outbound relationships and messages where X is the producer")]
    public static async Task<string> Execute(
        [Description("Project ID")] Guid projectId,
        [Description("Element ID")] Guid elementId,
        ModellingDbContext db,
        McpProjects projects,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var proj = await projects.ReadAsync(user, projectId, ct);

        var outbound = await db.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == proj.StreamId && r.FromId == elementId)
            .Select(r => new { r.Id, r.ToId, r.Label, r.Technology })
            .ToListAsync(ct);

        var producedMessages = await db.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == proj.StreamId && m.ProducerId == elementId)
            .Select(m => new { m.Id, m.Name, m.ConsumerIds, m.Tags })
            .ToListAsync(ct);

        var result = new
        {
            elementId,
            projectId,
            outboundRelationships = outbound,
            producedMessages
        };

        return JsonSerializer.Serialize(result);
    }
}
