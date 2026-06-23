using System.Text.Json;
using Vellum.Kernel.Results;

namespace Vellum.Modules.Schemas;

public sealed record CreateSchemaCommand(
    Guid Id, string Name, string? Description, string Content, Guid ProjectId);

public sealed record UpdateSchemaCommand(
    Guid SchemaId,
    string? Name = null, bool SetName = false,
    string? Description = null, bool SetDescription = false,
    string? Content = null, bool SetContent = false);

public static class SchemaDecider
{
    public static CommandResult<IReadOnlyList<SchemaEvent>> Create(SchemaState state, CreateSchemaCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<SchemaEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (state.Schema is not null)
            return new CommandResult<IReadOnlyList<SchemaEvent>>.Conflict("Schema already exists in this stream");

        var contentError = ValidateJsonSchema(cmd.Content);
        if (contentError is not null)
            return new CommandResult<IReadOnlyList<SchemaEvent>>.Invalid([contentError]);

        return new CommandResult<IReadOnlyList<SchemaEvent>>.Success(
            [new SchemaEvent.SchemaCreated(cmd.Id, cmd.Name, cmd.Description, cmd.Content, 1, cmd.ProjectId)]);
    }

    public static CommandResult<IReadOnlyList<SchemaEvent>> Update(SchemaState state, UpdateSchemaCommand cmd)
    {
        if (state.Schema is null || state.Deleted)
            return new CommandResult<IReadOnlyList<SchemaEvent>>.NotFound("Schema not found");

        if (cmd.SetName && string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<SchemaEvent>>.Invalid([new ValidationError("name", "Name is required")]);

        if (cmd.SetContent && cmd.Content is not null)
        {
            var contentError = ValidateJsonSchema(cmd.Content);
            if (contentError is not null)
                return new CommandResult<IReadOnlyList<SchemaEvent>>.Invalid([contentError]);
        }

        var hasChanges =
            (cmd.SetName && cmd.Name != state.Schema.Name) ||
            (cmd.SetDescription && cmd.Description != state.Schema.Description) ||
            (cmd.SetContent && cmd.Content != state.Schema.Content);

        if (!hasChanges)
            return new CommandResult<IReadOnlyList<SchemaEvent>>.Success([]);

        var newVersion = cmd.SetContent && cmd.Content != state.Schema.Content
            ? state.Schema.Version + 1
            : (int?)null;

        return new CommandResult<IReadOnlyList<SchemaEvent>>.Success(
            [new SchemaEvent.SchemaUpdated(
                cmd.SchemaId,
                cmd.SetName ? cmd.Name : null,
                cmd.SetDescription ? cmd.Description : null,
                cmd.SetContent ? cmd.Content : null,
                newVersion)]);
    }

    public static CommandResult<IReadOnlyList<SchemaEvent>> Delete(SchemaState state, Guid schemaId)
    {
        if (state.Schema is null || state.Deleted)
            return new CommandResult<IReadOnlyList<SchemaEvent>>.NotFound("Schema not found");

        return new CommandResult<IReadOnlyList<SchemaEvent>>.Success(
            [new SchemaEvent.SchemaDeleted(schemaId)]);
    }

    private static ValidationError? ValidateJsonSchema(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ValidationError("content", "JSON Schema must be a JSON object");
            if (!root.TryGetProperty("type", out _) &&
                !root.TryGetProperty("properties", out _) &&
                !root.TryGetProperty("$schema", out _))
                return new ValidationError("content", "JSON Schema must have a 'type', 'properties', or '$schema' property");
            return null;
        }
        catch (JsonException)
        {
            return new ValidationError("content", "Content is not valid JSON");
        }
    }
}
