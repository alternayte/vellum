using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record UpdateSchemaRequest(
    string? Name, string? Description, string? Content,
    bool SetDescription = false, bool SetContent = false);

public static class UpdateSchema
{
    public static async Task<CommandResult<SchemaDto>> HandleAsync(
        IEventStore store, AppDbContext db, Guid projectId, Guid schemaId, UpdateSchemaRequest request, CancellationToken ct)
    {
        // Verify schema belongs to project
        var belongsToProject = await db.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == schemaId && s.ProjectId == projectId, ct);
        if (!belongsToProject)
            return new CommandResult<SchemaDto>.NotFound("Schema not found");

        var updateCmd = new UpdateSchemaCommand(
            schemaId,
            Name: request.Name, SetName: request.Name is not null,
            Description: request.Description, SetDescription: request.SetDescription || request.Description is not null,
            Content: request.Content, SetContent: request.SetContent || request.Content is not null);

        return await store.Decide<SchemaState, SchemaDto>(
            schemaId,
            state => SchemaDecider.Update(state, updateCmd),
            state => CreateSchema.ToDto(state.Schema!),
            ct);
    }
}
