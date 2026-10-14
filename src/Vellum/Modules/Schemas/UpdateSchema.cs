using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Schemas;

public sealed record UpdateSchemaRequest(
    string? Name, string? Description, string? Content,
    bool SetDescription = false, bool SetContent = false);

public sealed record UpdateSchemaCommandEnvelope(Guid ProjectId, Guid SchemaId, UpdateSchemaRequest Request);

public sealed class UpdateSchemaHandler : ICommandHandler<UpdateSchemaCommandEnvelope, CommandResult<SchemaDto>>
{
    private readonly IEventStore _store;
    private readonly AppDbContext _db;

    public UpdateSchemaHandler(IEventStore store, AppDbContext db)
    {
        _store = store;
        _db = db;
    }

    public async Task<CommandResult<SchemaDto>> HandleAsync(UpdateSchemaCommandEnvelope cmd, CancellationToken ct = default)
    {
        // Verify schema belongs to project
        var belongsToProject = await _db.Schemas.AsNoTracking()
            .AnyAsync(s => s.Id == cmd.SchemaId && s.ProjectId == cmd.ProjectId, ct);
        if (!belongsToProject)
            return new CommandResult<SchemaDto>.NotFound("Schema not found");

        var updateCmd = new UpdateSchemaCommand(
            cmd.SchemaId,
            Name: cmd.Request.Name, SetName: cmd.Request.Name is not null,
            Description: cmd.Request.Description, SetDescription: cmd.Request.SetDescription || cmd.Request.Description is not null,
            Content: cmd.Request.Content, SetContent: cmd.Request.SetContent || cmd.Request.Content is not null);

        return await _store.Decide<SchemaState, SchemaDto>(
            cmd.SchemaId,
            state => SchemaDecider.Update(state, updateCmd),
            state => CreateSchemaHandler.ToDto(state.Schema!),
            ct);
    }
}
