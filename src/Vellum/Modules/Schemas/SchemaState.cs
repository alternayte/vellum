using Deedbox;
using Vellum.Modules.Modelling;

namespace Vellum.Modules.Schemas;

/// <param name="FactStatus">The truth label: <c>declared</c> until a <see cref="SchemaEvent.SchemaProvenanceRecorded"/> gives another.</param>
/// <param name="Provenance">Where the schema came from. Null for a schema that a person wrote.</param>
public sealed record SchemaStateEntry(
    Guid Id, string Name, string? Description,
    string Content, int Version, Guid ProjectId,
    FactStatus FactStatus = FactStatus.Declared, Provenance? Provenance = null);

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
        SchemaEvent.SchemaProvenanceRecorded e when state.Schema is { } schema => state with
        {
            Schema = schema with { FactStatus = e.Status, Provenance = e.Provenance }
        },
        SchemaEvent.SchemaDeleted => state with { Schema = null, Deleted = true },
        _ => state
    };
}
