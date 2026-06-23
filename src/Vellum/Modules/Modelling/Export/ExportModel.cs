using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Schemas;
using Vellum.Modules.Workspaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vellum.Modules.Modelling.Export;

public static class ExportModel
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static async Task<IResult> Handle(
        Guid projectId, string format,
        ModellingDbContext modellingDb,
        SchemasDbContext schemasDb,
        WorkspacesDbContext workspacesDb,
        CancellationToken ct)
    {
        var project = await workspacesDb.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return Results.NotFound();

        var elements = await modellingDb.Elements.AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Branch == project.StreamId)
            .Select(e => new ElementExport(e.Id, e.Kind, e.Name, e.Description,
                e.Technology, e.Status, e.ParentId, e.Tags))
            .ToListAsync(ct);

        var relationships = await modellingDb.Relationships.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Branch == project.StreamId)
            .Select(r => new RelationshipExport(r.Id, r.FromId, r.ToId, r.Label, r.Technology))
            .ToListAsync(ct);

        var messages = await modellingDb.Messages.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Branch == project.StreamId)
            .Select(m => new MessageExport(m.Id, m.Name, m.Description,
                m.ProducerId, m.ConsumerIds, m.SchemaId, m.Tags))
            .ToListAsync(ct);

        var schemas = await schemasDb.Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => new SchemaExport(s.Id, s.Name, s.Description, s.Content, s.Version))
            .ToListAsync(ct);

        var doc = new ModelExportDocument("1.0",
            new ProjectInfo(project.Name), elements, relationships, messages, schemas);

        if (format.Equals("yaml", StringComparison.OrdinalIgnoreCase))
        {
            var yaml = YamlSerializer.Serialize(doc);
            return Results.Text(yaml, "application/x-yaml");
        }

        return Results.Json(doc, JsonOpts);
    }
}
