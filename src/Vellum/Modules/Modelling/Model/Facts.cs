using Vellum.Shared;

namespace Vellum.Modules.Modelling.Model;

/// <summary>
/// How a fact's truth is established. SDD guarantee 2: every fact is labelled, and an
/// inference is never silently promoted to fact.
/// </summary>
public enum FactStatus
{
    Declared = 0,
    ObservedInSource = 1,
    Inferred = 2,
    Measured = 3,
    Unknown = 4,
}

/// <summary>
/// Where a fact came from. <paramref name="Source"/> is a stable pointer such as
/// <c>github.com/acme/orders@abc123:src/Api/Orders.cs:41-57</c>; <paramref name="Revision"/>
/// pins the revision the source was read at; <paramref name="Confidence"/> is required for
/// inferred facts; <paramref name="AcceptedBy"/> and <paramref name="AcceptedAt"/> record
/// who accepted the claim and when.
/// </summary>
public sealed record Provenance(
    string? Source = null,
    string? Revision = null,
    double? Confidence = null,
    Guid? AcceptedBy = null,
    DateTimeOffset? AcceptedAt = null);

/// <summary>
/// A mark on an accepted fact whose evidence a complete repository scan no longer finds. It
/// deletes nothing and changes no value of the fact. <paramref name="SinceRevision"/> is the
/// commit SHA of the first complete scan that did not find the evidence;
/// <paramref name="Reason"/> is a fixed code such as <c>evidence-not-found</c>.
/// </summary>
public sealed record StaleMark(string SinceRevision, string Reason);

/// <summary>A stale mark as returned by the API.</summary>
public sealed record StaleDto(string SinceRevision, string Reason)
{
    public static StaleDto? From(StaleMark? mark) => mark is null ? null : new(mark.SinceRevision, mark.Reason);

    public static StaleDto? From(string? sinceRevision, string? reason) =>
        sinceRevision is null || reason is null ? null : new(sinceRevision, reason);
}

/// <summary>Provenance as supplied by a caller. Acceptance stamps are set by the server.</summary>
public sealed record ProvenanceInput(string? Source, string? Revision, double? Confidence);

/// <summary>Provenance as returned by the API.</summary>
public sealed record ProvenanceDto(
    string? Source, string? Revision, double? Confidence,
    Guid? AcceptedBy, DateTimeOffset? AcceptedAt);

public static class FactStatusFormat
{
    public static string ToApiString(this FactStatus status) => status switch
    {
        FactStatus.ObservedInSource => "observed-in-source",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static bool TryParseApiString(string value, out FactStatus status) =>
        ModelEnum.TryParseDefined(value, out status);
}

public static class ModelEnum
{
    /// <summary>
    /// Parses an enum member name (hyphens/underscores ignored, case-insensitive) and rejects
    /// numeric strings and undefined members, so closed label sets stay closed.
    /// </summary>
    public static bool TryParseDefined<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        if (value is null) return false;

        var normalized = value.Replace("-", "").Replace("_", "");
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var match = Enum.GetNames<T>().FirstOrDefault(
            name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;

        parsed = Enum.Parse<T>(match);
        return true;
    }
}

public static class ProvenanceMapper
{
    public static ProvenanceDto? ToDto(Provenance? provenance) =>
        provenance is null
            ? null
            : new ProvenanceDto(
                provenance.Source, provenance.Revision, provenance.Confidence,
                provenance.AcceptedBy, provenance.AcceptedAt);

    public static ProvenanceDto? ToDto(
        string? source, string? revision, double? confidence, Guid? acceptedBy, DateTimeOffset? acceptedAt) =>
        source is null && revision is null && confidence is null && acceptedBy is null && acceptedAt is null
            ? null
            : new ProvenanceDto(source, revision, confidence, acceptedBy, acceptedAt);

    public static Provenance? ToDomain(ProvenanceInput? input, Guid actorId) =>
        input is null
            ? null
            : new Provenance(input.Source, input.Revision, input.Confidence, actorId, DateTimeOffset.UtcNow);
}

public static class FactRules
{
    public static ValidationError? Validate(FactStatus status, Provenance? provenance)
    {
        if (provenance is null)
        {
            return status switch
            {
                FactStatus.ObservedInSource =>
                    new ValidationError("provenance", "Observed-in-source facts require a source pointer"),
                FactStatus.Inferred =>
                    new ValidationError("provenance", "Inferred facts require a source pointer and a confidence"),
                FactStatus.Measured =>
                    new ValidationError("provenance", "Measured facts require an evidence pointer"),
                _ => null,
            };
        }

        if (provenance.Source is not null && string.IsNullOrWhiteSpace(provenance.Source))
            return new ValidationError("provenance.source", "Source must not be blank");

        if (provenance.Revision is not null && string.IsNullOrWhiteSpace(provenance.Revision))
            return new ValidationError("provenance.revision", "Revision must not be blank");

        var textError = TextRule.Line("provenance.source", provenance.Source, TextRule.MaxPointer)
            ?? TextRule.Line("provenance.revision", provenance.Revision);
        if (textError is not null)
            return textError;

        if (provenance.Confidence is < 0 or > 1)
            return new ValidationError("provenance.confidence", "Confidence must be between 0 and 1");

        return status switch
        {
            FactStatus.ObservedInSource when string.IsNullOrWhiteSpace(provenance.Source) =>
                new ValidationError("provenance.source", "Observed-in-source facts require a source pointer"),
            FactStatus.Inferred when string.IsNullOrWhiteSpace(provenance.Source) =>
                new ValidationError("provenance.source", "Inferred facts require a source pointer"),
            FactStatus.Inferred when provenance.Confidence is null =>
                new ValidationError("provenance.confidence", "Inferred facts require a confidence"),
            FactStatus.Measured when string.IsNullOrWhiteSpace(provenance.Source) =>
                new ValidationError("provenance.source", "Measured facts require an evidence pointer"),
            _ => null,
        };
    }
}
