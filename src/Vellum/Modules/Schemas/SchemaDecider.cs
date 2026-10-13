using System.Text.Json;
using Vellum.Shared;

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
    /// <summary>
    /// The most characters one schema document holds. Every change of the content stores the
    /// whole document again, and a scenario run compiles it.
    /// </summary>
    public const int MaxContentLength = 256 * 1024;

    public static CommandResult<IReadOnlyList<object>> Create(SchemaState state, CreateSchemaCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);

        if (cmd.Id == Guid.Empty)
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("id", "An ID is required")]);

        if ((TextRule.Line("name", cmd.Name) ?? TextRule.Lines("description", cmd.Description)) is { } textError)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (state.Schema is not null)
            return new CommandResult<IReadOnlyList<object>>.Conflict("Schema already exists in this stream");

        var contentError = ValidateJsonSchema(cmd.Content);
        if (contentError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([contentError]);

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new SchemaEvent.SchemaCreated(cmd.Id, cmd.Name, cmd.Description, cmd.Content, 1, cmd.ProjectId)]);
    }

    public static CommandResult<IReadOnlyList<object>> Update(SchemaState state, UpdateSchemaCommand cmd)
    {
        if (state.Schema is null || state.Deleted)
            return new CommandResult<IReadOnlyList<object>>.NotFound("Schema not found");

        if (cmd.SetName && string.IsNullOrWhiteSpace(cmd.Name))
            return new CommandResult<IReadOnlyList<object>>.Invalid([new ValidationError("name", "Name is required")]);

        var textError =
            (cmd.SetName ? TextRule.Line("name", cmd.Name) : null)
            ?? (cmd.SetDescription ? TextRule.Lines("description", cmd.Description) : null);
        if (textError is not null)
            return new CommandResult<IReadOnlyList<object>>.Invalid([textError]);

        if (cmd.SetContent && cmd.Content is not null)
        {
            var contentError = ValidateJsonSchema(cmd.Content);
            if (contentError is not null)
                return new CommandResult<IReadOnlyList<object>>.Invalid([contentError]);
        }

        var hasChanges =
            (cmd.SetName && cmd.Name != state.Schema.Name) ||
            (cmd.SetDescription && cmd.Description != state.Schema.Description) ||
            (cmd.SetContent && cmd.Content != state.Schema.Content);

        if (!hasChanges)
            return new CommandResult<IReadOnlyList<object>>.Success([]);

        var newVersion = cmd.SetContent && cmd.Content != state.Schema.Content
            ? state.Schema.Version + 1
            : (int?)null;

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new SchemaEvent.SchemaUpdated(
                cmd.SchemaId,
                cmd.SetName ? cmd.Name : null,
                cmd.SetDescription ? cmd.Description : null,
                cmd.SetContent ? cmd.Content : null,
                newVersion)]);
    }

    public static CommandResult<IReadOnlyList<object>> Delete(SchemaState state, Guid schemaId)
    {
        if (state.Schema is null || state.Deleted)
            return new CommandResult<IReadOnlyList<object>>.NotFound("Schema not found");

        return new CommandResult<IReadOnlyList<object>>.Success(
            [new SchemaEvent.SchemaDeleted(schemaId)]);
    }

    private static ValidationError? ValidateJsonSchema(string content)
    {
        if (content is null)
            return new ValidationError("content", "Content is required");
        if (content.Length > MaxContentLength)
            return new ValidationError("content", $"A schema holds at most {MaxContentLength} characters; this one has {content.Length}");

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
