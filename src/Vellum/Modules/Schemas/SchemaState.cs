using Vellum.Kernel.Aggregates;

namespace Vellum.Modules.Schemas;

public sealed record SchemaStateEntry(
    Guid Id, string Name, string? Description,
    string Content, int Version, Guid ProjectId);

public sealed record SchemaState(SchemaStateEntry? Schema, bool Deleted)
    : IAggregateState<SchemaState, SchemaEvent>
{
    public static SchemaState Initial => new(null, false);

    public SchemaState Evolve(SchemaEvent @event) => @event switch
    {
        SchemaEvent.SchemaCreated e => this with
        {
            Schema = new SchemaStateEntry(e.Id, e.Name, e.Description, e.Content, e.Version, e.ProjectId)
        },
        SchemaEvent.SchemaUpdated e => this with
        {
            Schema = Schema! with
            {
                Name = e.Name ?? Schema.Name,
                Description = e.Description ?? Schema.Description,
                Content = e.Content ?? Schema.Content,
                Version = e.Version ?? Schema.Version,
            }
        },
        SchemaEvent.SchemaDeleted => this with { Schema = null, Deleted = true },
        _ => this
    };
}
