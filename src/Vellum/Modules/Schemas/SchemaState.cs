using Deedbox;

namespace Vellum.Modules.Schemas;

public sealed record SchemaStateEntry(
    Guid Id, string Name, string? Description,
    string Content, int Version, Guid ProjectId);

public sealed record SchemaState(SchemaStateEntry? Schema, bool Deleted) : IState<SchemaState>
{
    public static SchemaState Initial => new(null, false);

    public static SchemaState Evolve(SchemaState state, object @event) => @event switch
    {
        SchemaEvent.SchemaCreated e => state with
        {
            Schema = new SchemaStateEntry(e.Id, e.Name, e.Description, e.Content, e.Version, e.ProjectId)
        },
        SchemaEvent.SchemaUpdated e when state.Schema is { } schema => state with
        {
            Schema = schema with
            {
                Name = e.Name ?? schema.Name,
                Description = e.Description ?? schema.Description,
                Content = e.Content ?? schema.Content,
                Version = e.Version ?? schema.Version,
            }
        },
        SchemaEvent.SchemaDeleted => state with { Schema = null, Deleted = true },
        _ => state
    };
}
