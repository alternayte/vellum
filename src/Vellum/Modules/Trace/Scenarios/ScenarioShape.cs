using System.Runtime.InteropServices;
using System.Text.Json;
using Vellum.Modules.Trace.Engine;
using Vellum.Shared;

namespace Vellum.Modules.Trace.Scenarios;

/// <summary>
/// What a scenario document must satisfy to be stored. These are limits of structure and
/// size only: a draft that names a contract that does not exist, repeats a step ID or fails a
/// schema is still saved, and the run reports it. A document inside these limits always fits
/// the stored event, the read-model row and the engine's <see cref="TraceLimits"/>.
/// </summary>
public static class ScenarioShape
{
    /// <summary>The most bytes a document holds when it is written as JSON.</summary>
    public const int MaxDocumentBytes = 512 * 1024;

    /// <summary>The most arrays and objects one payload nests inside each other.</summary>
    public const int MaxPayloadDepth = 32;

    public const int MaxHeaders = 50;

    /// <summary>
    /// The JSON depth that every reader and writer of a scenario allows. The deepest document
    /// the rules below accept has 104 levels: the document and its step list, three levels for
    /// each of the 23 further step lists (a publish step's handler map, the list and the step),
    /// the step itself and a payload of <see cref="MaxPayloadDepth"/>. The stored event and the
    /// stored state each add one.
    /// </summary>
    public const int MaxJsonDepth = 128;

    /// <summary>The first rule the document breaks, with the path of the value, or null.</summary>
    public static ValidationError? Check(Scenario scenario)
    {
        if (TextRule.Line("name", scenario.Name) is { } nameError)
            return nameError;

        var count = 0;
        if (Sequence(scenario.Steps, "steps", 1, ref count) is { } stepError)
            return stepError;
        if (count > TraceLimits.MaxSteps)
        {
            return new ValidationError("steps",
                $"A scenario has at most {TraceLimits.MaxSteps} steps and step lists; this one has {count}");
        }

        var index = 0;
        foreach (var assertion in scenario.Assertions ?? [])
        {
            var path = $"assertions[{index++}]";
            var error = TextRule.Line($"{path}.id", assertion?.Id, TextRule.MaxIdentifier)
                ?? (assertion is OutcomeAssertion outcome ? TextRule.Line($"{path}.step", outcome.Step, TextRule.MaxIdentifier) : null);
            if (error is not null)
                return error;
        }

        return null;
    }

    // Counts as the engine's validator does: every list and every step is one unit of work.
    private static ValidationError? Sequence(IReadOnlyList<Step>? steps, string path, int depth, ref int count)
    {
        if (steps is null)
            return null;
        if (depth > TraceLimits.MaxDepth)
            return new ValidationError(path, $"Steps nest at most {TraceLimits.MaxDepth} levels");

        count++;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not { } step)
                continue;

            count++;
            if (count > TraceLimits.MaxSteps)
                return null;

            var at = $"{path}[{i}]";
            if (TextRule.Line($"{at}.id", step.Id, TextRule.MaxIdentifier) is { } idError)
                return idError;

            var error = step switch
            {
                CallStep call =>
                    Payload(call.Request, $"{at}.request")
                    ?? Payload(call.Response, $"{at}.response")
                    ?? Sequence(call.Then, $"{at}.then", depth + 1, ref count)
                    ?? Sequence(call.OnError, $"{at}.onError", depth + 1, ref count),
                PublishStep publish =>
                    Payload(publish.Payload, $"{at}.payload")
                    ?? Headers(publish.Headers, $"{at}.headers")
                    ?? Handlers(publish, at, depth, ref count),
                WriteStep write =>
                    TextRule.Line($"{at}.dataset", write.Dataset)
                    ?? Payload(write.Change, $"{at}.change"),
                ParallelStep parallel => Branches(parallel, at, depth, ref count),
                BranchStep branch =>
                    TextRule.Line($"{at}.whenStep", branch.WhenStep, TextRule.MaxIdentifier)
                    ?? Sequence(branch.Then, $"{at}.then", depth + 1, ref count)
                    ?? Sequence(branch.Else, $"{at}.else", depth + 1, ref count),
                _ => null,
            };
            if (error is not null)
                return error;
        }

        return null;
    }

    private static ValidationError? Handlers(PublishStep publish, string at, int depth, ref int count)
    {
        if (publish.Handlers is null)
            return null;

        foreach (var (subscription, handler) in publish.Handlers)
        {
            if (Sequence(handler, $"{at}.handlers.{subscription}", depth + 1, ref count) is { } error)
                return error;
        }

        return null;
    }

    private static ValidationError? Branches(ParallelStep parallel, string at, int depth, ref int count)
    {
        var index = 0;
        foreach (var branch in parallel.Branches ?? [])
        {
            if (Sequence(branch, $"{at}.branches[{index++}]", depth + 1, ref count) is { } error)
                return error;
        }

        return null;
    }

    private static ValidationError? Headers(IReadOnlyDictionary<string, string>? headers, string path)
    {
        if (headers is null)
            return null;
        if (headers.Count > MaxHeaders)
            return new ValidationError(path, $"A message has at most {MaxHeaders} headers; this one has {headers.Count}");

        foreach (var (name, value) in headers)
        {
            var error = TextRule.Line(path, name) ?? TextRule.Line($"{path}.{name}", value, TextRule.MaxPointer);
            if (error is not null)
                return error;
        }

        return null;
    }

    /// <summary>
    /// A payload is free JSON, so its strings may hold any text but two kinds that the event
    /// store cannot keep: U+0000 and half of a surrogate pair. A number must have a finite
    /// value and at most <see cref="CanonicalJson.MaxNumberChars"/> characters, so that it has
    /// a canonical form (<see cref="CanonicalJson.Defect"/>).
    /// </summary>
    internal static ValidationError? Payload(JsonElement? payload, string path)
    {
        if (payload is not { } value)
            return null;

        var bytes = JsonMarshal.GetRawUtf8Value(value).Length;
        if (bytes > TraceLimits.MaxPayloadBytes)
        {
            return new ValidationError(path,
                $"A payload holds at most {TraceLimits.MaxPayloadBytes} bytes; this one has {bytes}. Use a representative fixture");
        }

        return Value(value, path, 0);
    }

    private static ValidationError? Value(JsonElement value, string path, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= MaxPayloadDepth)
                    return new ValidationError(path, $"A payload nests at most {MaxPayloadDepth} levels");
                foreach (var property in value.EnumerateObject())
                {
                    var error = Text(() => property.Name, path) ?? Value(property.Value, path, depth + 1);
                    if (error is not null)
                        return error;
                }
                return null;

            case JsonValueKind.Array:
                if (depth >= MaxPayloadDepth)
                    return new ValidationError(path, $"A payload nests at most {MaxPayloadDepth} levels");
                foreach (var item in value.EnumerateArray())
                {
                    if (Value(item, path, depth + 1) is { } error)
                        return error;
                }
                return null;

            case JsonValueKind.String:
                return Text(value.GetString, path);

            // A number that has no finite value, or that is too long, has no canonical form.
            case JsonValueKind.Number:
                return CanonicalJson.Defect(value) is { } defect ? new ValidationError(path, $"The payload {defect}") : null;

            default:
                return null;
        }
    }

    private static ValidationError? Text(Func<string?> read, string path)
    {
        string? text;
        try
        {
            text = read();
        }
        // The reader refuses an escape that is half of a surrogate pair.
        catch (InvalidOperationException)
        {
            return new ValidationError(path, "The payload holds text that is not valid Unicode");
        }

        return text is not null && text.Contains('\0')
            ? new ValidationError(path, "The payload holds the character U+0000, which cannot be stored")
            : null;
    }
}
