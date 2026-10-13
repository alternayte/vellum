using Vellum.Modules.Modelling.Model;

namespace Vellum.Modules.Modelling.Export;

public sealed record ModelExportDocument(
    string Version,
    ProjectInfo Project,
    IReadOnlyList<ElementExport> Elements,
    IReadOnlyList<RelationshipExport> Relationships,
    IReadOnlyList<MessageExport> Messages,
    IReadOnlyList<SchemaExport> Schemas,
    IReadOnlyList<OperationExport> Operations,
    IReadOnlyList<ChannelExport> Channels,
    IReadOnlyList<SubscriptionExport> Subscriptions,
    // Added after the first documents were written: a document without the member has no bindings.
    IReadOnlyList<BindingExport>? Bindings = null);

public sealed record ProjectInfo(string Name);

public sealed record ProvenanceExport(
    string? Source, string? Revision, double? Confidence,
    Guid? AcceptedBy, DateTimeOffset? AcceptedAt)
{
    public static ProvenanceExport? From(Provenance? provenance) =>
        provenance is null
            ? null
            : new ProvenanceExport(
                provenance.Source, provenance.Revision, provenance.Confidence,
                provenance.AcceptedBy, provenance.AcceptedAt);

    public static ProvenanceExport? From(
        string? source, string? revision, double? confidence, Guid? acceptedBy, DateTimeOffset? acceptedAt) =>
        source is null && revision is null && confidence is null && acceptedBy is null && acceptedAt is null
            ? null
            : new ProvenanceExport(source, revision, confidence, acceptedBy, acceptedAt);

    public Provenance ToDomain() => new(Source, Revision, Confidence, AcceptedBy, AcceptedAt);
}

/// <summary>
/// The stale mark of a fact. The member is optional on every fact kind: a fact without it,
/// and every fact of a document written before the member existed, has no mark. The members
/// are nullable because a document is untrusted; <see cref="ImportModel"/> requires both.
/// </summary>
public sealed record StaleExport(string? SinceRevision, string? Reason)
{
    public static StaleExport? From(StaleMark? mark) => mark is null ? null : new(mark.SinceRevision, mark.Reason);

    public static StaleExport? From(string? sinceRevision, string? reason) =>
        sinceRevision is null || reason is null ? null : new(sinceRevision, reason);
}

public sealed record ElementExport(
    Guid Id, string Kind, string Name, string? Description,
    string? Technology, string Status, Guid? ParentId, string[] Tags,
    string? Icon = null,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record RelationshipExport(
    Guid Id, Guid FromId, Guid ToId, string? Label, string? Technology,
    string Kind = "structural", string? LineShape = null,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record MessageExport(
    Guid Id, string Name, string? Description,
    Guid ProducerId, Guid[] ConsumerIds, Guid? SchemaId, string[] Tags,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record SchemaExport(
    Guid Id, string Name, string? Description, string Content, int Version);

public sealed record RetryExport(int MaxAttempts, int BackoffMs);

public sealed record OperationExport(
    Guid Id, Guid OwnerId, string Name, string Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int TimeoutMs, RetryExport? Retry,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record ChannelExport(
    Guid Id, string Name, string Kind,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record SubscriptionExport(
    Guid Id, Guid ChannelId, Guid ConsumerId,
    RetryExport? Retry, Guid? DeadLetterChannelId,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);

public sealed record BindingExport(
    Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey = null,
    string FactStatus = "declared", ProvenanceExport? Provenance = null,
    StaleExport? Stale = null);
