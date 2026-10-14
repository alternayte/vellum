using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

namespace Vellum.Modules.Modelling;

/// <summary>
/// The model read model: one row per element, relationship and message on each open branch.
/// It runs inline, so the rows commit in the same transaction as the events.
/// <para>
/// A row needs two facts that a model event does not carry. The branch is the stream ID.
/// The project travels as the <see cref="ModelStore.ProjectHeader"/> metadata header, which
/// Deedbox stores with the event, so a rebuild reads it back.
/// </para>
/// </summary>
public sealed class ModelProjection : Projection<AppDbContext>
{
    public ModelProjection()
    {
        On<ModelEvent.BranchForked>((forked, ctx) =>
        {
            WriteBranch(ProjectOf(ctx), BranchOf(ctx), forked, ctx.Db);
            return Task.CompletedTask;
        });

        On<ModelEvent.BranchClosed>(async (closed, ctx) =>
        {
            // Deedbox saves once per append, and once per batch in a rebuild. Rows of the
            // branch that an earlier event of the same batch added are not in the database
            // yet, so they leave the change tracker here; the stored rows are deleted.
            foreach (var entry in ctx.Db.ChangeTracker.Entries().ToList())
            {
                var branch = entry.Entity switch
                {
                    ElementEntity element => element.Branch,
                    RelationshipEntity relationship => relationship.Branch,
                    MessageEntity message => message.Branch,
                    _ => (Guid?)null,
                };
                if (branch == closed.BranchId)
                    entry.State = EntityState.Detached;
            }

            await ctx.Db.Elements.Where(e => e.Branch == closed.BranchId).ExecuteDeleteAsync(ctx.CancellationToken);
            await ctx.Db.Relationships.Where(r => r.Branch == closed.BranchId).ExecuteDeleteAsync(ctx.CancellationToken);
            await ctx.Db.Messages.Where(m => m.Branch == closed.BranchId).ExecuteDeleteAsync(ctx.CancellationToken);
        });

        On<ModelEvent.ElementAdded>((added, ctx) =>
        {
            ctx.Db.Elements.Add(new ElementEntity
            {
                Id = added.Id,
                ProjectId = ProjectOf(ctx),
                Branch = BranchOf(ctx),
                Kind = added.Kind.ToString().ToLowerInvariant(),
                Name = added.Name,
                Description = added.Description,
                Technology = added.Technology,
                OwnerId = added.OwnerId,
                Status = added.Status.ToString().ToLowerInvariant(),
                ParentId = added.ParentId,
                Tags = added.Tags,
                Icon = null
            });
            return Task.CompletedTask;
        });

        OnElement<ModelEvent.ElementRenamed>(e => e.ElementId, (e, row) => row.Name = e.Name);
        OnElement<ModelEvent.ElementDescriptionChanged>(e => e.ElementId, (e, row) => row.Description = e.Description);
        OnElement<ModelEvent.ElementTechnologyChanged>(e => e.ElementId, (e, row) => row.Technology = e.Technology);
        OnElement<ModelEvent.ElementOwnerChanged>(e => e.ElementId, (e, row) => row.OwnerId = e.OwnerId);
        OnElement<ModelEvent.ElementReparented>(e => e.ElementId, (e, row) => row.ParentId = e.ParentId);
        OnElement<ModelEvent.ElementStatusChanged>(e => e.ElementId, (e, row) => row.Status = e.Status.ToString().ToLowerInvariant());
        OnElement<ModelEvent.ElementRetagged>(e => e.ElementId, (e, row) => row.Tags = e.Tags);
        OnElement<ModelEvent.ElementIconChanged>(e => e.ElementId, (e, row) => row.Icon = e.Icon);
        OnElement<ModelEvent.ElementProvenanceRecorded>(e => e.ElementId, (e, row) =>
        {
            row.FactStatus = e.Status.ToApiString();
            row.ProvenanceSource = e.Provenance?.Source;
            row.ProvenanceRevision = e.Provenance?.Revision;
            row.ProvenanceConfidence = e.Provenance?.Confidence;
            row.ProvenanceAcceptedBy = e.Provenance?.AcceptedBy;
            row.ProvenanceAcceptedAt = e.Provenance?.AcceptedAt;
            row.ProvenanceAcceptedAtVersion = e.Provenance?.AcceptedAtVersion;
            row.ProvenanceAcceptedOnBranch = e.Provenance?.AcceptedOnBranch;
            row.StaleSinceRevision = e.Stale?.SinceRevision;
            row.StaleReason = e.Stale?.Reason;
        });
        On<ModelEvent.ElementRemoved>(async (removed, ctx) =>
        {
            var row = await FindElementAsync(ctx, removed.ElementId);
            if (row is not null) ctx.Db.Elements.Remove(row);
        });

        On<ModelEvent.RelationshipAdded>((added, ctx) =>
        {
            ctx.Db.Relationships.Add(new RelationshipEntity
            {
                Id = added.Id,
                ProjectId = ProjectOf(ctx),
                Branch = BranchOf(ctx),
                FromId = added.FromId,
                ToId = added.ToId,
                Label = added.Label,
                Technology = added.Technology,
                MessageId = added.MessageId,
                LineShape = null,
                Kind = added.Kind.ToString().ToLowerInvariant()
            });
            return Task.CompletedTask;
        });

        OnRelationship<ModelEvent.RelationshipLabelChanged>(e => e.RelationshipId, (e, row) => row.Label = e.Label);
        OnRelationship<ModelEvent.RelationshipTechnologyChanged>(e => e.RelationshipId, (e, row) => row.Technology = e.Technology);
        OnRelationship<ModelEvent.RelationshipLineShapeChanged>(e => e.RelationshipId, (e, row) => row.LineShape = e.LineShape);
        OnRelationship<ModelEvent.RelationshipKindChanged>(e => e.RelationshipId, (e, row) => row.Kind = e.Kind.ToString().ToLowerInvariant());
        OnRelationship<ModelEvent.RelationshipProvenanceRecorded>(e => e.RelationshipId, (e, row) =>
        {
            row.FactStatus = e.Status.ToApiString();
            row.ProvenanceSource = e.Provenance?.Source;
            row.ProvenanceRevision = e.Provenance?.Revision;
            row.ProvenanceConfidence = e.Provenance?.Confidence;
            row.ProvenanceAcceptedBy = e.Provenance?.AcceptedBy;
            row.ProvenanceAcceptedAt = e.Provenance?.AcceptedAt;
            row.ProvenanceAcceptedAtVersion = e.Provenance?.AcceptedAtVersion;
            row.ProvenanceAcceptedOnBranch = e.Provenance?.AcceptedOnBranch;
            row.StaleSinceRevision = e.Stale?.SinceRevision;
            row.StaleReason = e.Stale?.Reason;
        });
        On<ModelEvent.RelationshipRemoved>(async (removed, ctx) =>
        {
            var row = await FindRelationshipAsync(ctx, removed.RelationshipId);
            if (row is not null) ctx.Db.Relationships.Remove(row);
        });

        On<ModelEvent.MessageAdded>((added, ctx) =>
        {
            ctx.Db.Messages.Add(new MessageEntity
            {
                Id = added.Id,
                ProjectId = ProjectOf(ctx),
                Branch = BranchOf(ctx),
                Name = added.Name,
                Description = added.Description,
                ProducerId = added.ProducerId,
                ConsumerIds = added.ConsumerIds.ToGuids(),
                SchemaId = added.SchemaId,
                Tags = added.Tags
            });
            return Task.CompletedTask;
        });

        OnMessage<ModelEvent.MessageUpdated>(e => e.MessageId, (e, row) =>
        {
            if (e.Name is not null) row.Name = e.Name;
            if (e.Description is not null) row.Description = e.Description;
            if (e.ProducerId is not null) row.ProducerId = e.ProducerId.Value;
            if (e.ConsumerIds is not null) row.ConsumerIds = e.ConsumerIds.ToGuids();
            if (e.SetSchemaId) row.SchemaId = e.SchemaId;
            if (e.Tags is not null) row.Tags = e.Tags;
        });
        OnMessage<ModelEvent.MessageProvenanceRecorded>(e => e.MessageId, (e, row) =>
        {
            row.FactStatus = e.Status.ToApiString();
            row.ProvenanceSource = e.Provenance?.Source;
            row.ProvenanceRevision = e.Provenance?.Revision;
            row.ProvenanceConfidence = e.Provenance?.Confidence;
            row.ProvenanceAcceptedBy = e.Provenance?.AcceptedBy;
            row.ProvenanceAcceptedAt = e.Provenance?.AcceptedAt;
            row.ProvenanceAcceptedAtVersion = e.Provenance?.AcceptedAtVersion;
            row.ProvenanceAcceptedOnBranch = e.Provenance?.AcceptedOnBranch;
            row.StaleSinceRevision = e.Stale?.SinceRevision;
            row.StaleReason = e.Stale?.Reason;
        });
        On<ModelEvent.MessageRemoved>(async (removed, ctx) =>
        {
            var row = await FindMessageAsync(ctx, removed.MessageId);
            if (row is not null) ctx.Db.Messages.Remove(row);
        });
    }

    /// <summary>Revisions and variants are not projected from events, so a rebuild keeps them.</summary>
    protected override async Task ResetAsync(WriteContext<AppDbContext> context)
    {
        await context.Db.Elements.ExecuteDeleteAsync(context.CancellationToken);
        await context.Db.Relationships.ExecuteDeleteAsync(context.CancellationToken);
        await context.Db.Messages.ExecuteDeleteAsync(context.CancellationToken);
    }

    private void OnElement<TEvent>(Func<TEvent, ElementId> id, Action<TEvent, ElementEntity> apply) where TEvent : notnull =>
        On<TEvent>(async (e, ctx) =>
        {
            var row = await FindElementAsync(ctx, id(e));
            if (row is not null) apply(e, row);
        });

    private void OnRelationship<TEvent>(Func<TEvent, RelationshipId> id, Action<TEvent, RelationshipEntity> apply) where TEvent : notnull =>
        On<TEvent>(async (e, ctx) =>
        {
            var row = await FindRelationshipAsync(ctx, id(e));
            if (row is not null) apply(e, row);
        });

    private void OnMessage<TEvent>(Func<TEvent, MessageId> id, Action<TEvent, MessageEntity> apply) where TEvent : notnull =>
        On<TEvent>(async (e, ctx) =>
        {
            var row = await FindMessageAsync(ctx, id(e));
            if (row is not null) apply(e, row);
        });

    private static Guid BranchOf(ProjectionContext ctx) => Guid.Parse(ctx.StreamId);

    private static Guid ProjectOf(ProjectionContext ctx) =>
        ctx.Metadata.Headers.TryGetValue(ModelStore.ProjectHeader, out var value) && Guid.TryParse(value, out var projectId)
            ? projectId
            : throw new InvalidOperationException(
                $"Model event {ctx.EventId} on stream {ctx.StreamId} has no '{ModelStore.ProjectHeader}' header. " +
                "Append model events through IEventStore.ForProject.");

    /// <summary>
    /// Branch-scoped lookups: the same architecture ID can exist on the main branch, a
    /// draft and a variant, so reads and writes must name the branch. Deedbox saves once per
    /// append, so rows added by an earlier event of the same append are found through the
    /// change tracker before the database.
    /// </summary>
    private static async Task<ElementEntity?> FindElementAsync(ProjectionContext<AppDbContext> ctx, ElementId id)
    {
        var branch = BranchOf(ctx);
        return ctx.Db.Elements.Local.FirstOrDefault(e => e.Id == id.Value && e.Branch == branch)
            ?? await ctx.Db.Elements.FirstOrDefaultAsync(e => e.Id == id.Value && e.Branch == branch, ctx.CancellationToken);
    }

    private static async Task<RelationshipEntity?> FindRelationshipAsync(ProjectionContext<AppDbContext> ctx, RelationshipId id)
    {
        var branch = BranchOf(ctx);
        return ctx.Db.Relationships.Local.FirstOrDefault(r => r.Id == id.Value && r.Branch == branch)
            ?? await ctx.Db.Relationships.FirstOrDefaultAsync(r => r.Id == id.Value && r.Branch == branch, ctx.CancellationToken);
    }

    private static async Task<MessageEntity?> FindMessageAsync(ProjectionContext<AppDbContext> ctx, MessageId id)
    {
        var branch = BranchOf(ctx);
        return ctx.Db.Messages.Local.FirstOrDefault(m => m.Id == id.Value && m.Branch == branch)
            ?? await ctx.Db.Messages.FirstOrDefaultAsync(m => m.Id == id.Value && m.Branch == branch, ctx.CancellationToken);
    }

    /// <summary>
    /// Writes complete rows for a forked branch so a draft or variant reads as a full model,
    /// not just the changes made on it. The synthetic row ID is assigned automatically.
    /// </summary>
    private static void WriteBranch(Guid projectId, Guid branch, ModelEvent.BranchForked forked, AppDbContext db)
    {
        foreach (var element in forked.Elements)
        {
            db.Elements.Add(new ElementEntity
            {
                Id = element.Id,
                ProjectId = projectId,
                Branch = branch,
                Kind = element.Kind.ToString().ToLowerInvariant(),
                Name = element.Name,
                Description = element.Description,
                Technology = element.Technology,
                OwnerId = element.OwnerId,
                Status = element.Status.ToString().ToLowerInvariant(),
                ParentId = element.ParentId,
                Tags = element.Tags,
                Icon = element.Icon,
                FactStatus = element.FactStatus.ToApiString(),
                ProvenanceSource = element.Provenance?.Source,
                ProvenanceRevision = element.Provenance?.Revision,
                ProvenanceConfidence = element.Provenance?.Confidence,
                ProvenanceAcceptedBy = element.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = element.Provenance?.AcceptedAt,
                ProvenanceAcceptedAtVersion = element.Provenance?.AcceptedAtVersion,
                ProvenanceAcceptedOnBranch = element.Provenance?.AcceptedOnBranch,
                StaleSinceRevision = element.Stale?.SinceRevision,
                StaleReason = element.Stale?.Reason,
            });
        }

        foreach (var relationship in forked.Relationships)
        {
            db.Relationships.Add(new RelationshipEntity
            {
                Id = relationship.Id,
                ProjectId = projectId,
                Branch = branch,
                FromId = relationship.FromId,
                ToId = relationship.ToId,
                Label = relationship.Label,
                Technology = relationship.Technology,
                MessageId = relationship.MessageId,
                LineShape = relationship.LineShape,
                Kind = relationship.Kind.ToString().ToLowerInvariant(),
                FactStatus = relationship.FactStatus.ToApiString(),
                ProvenanceSource = relationship.Provenance?.Source,
                ProvenanceRevision = relationship.Provenance?.Revision,
                ProvenanceConfidence = relationship.Provenance?.Confidence,
                ProvenanceAcceptedBy = relationship.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = relationship.Provenance?.AcceptedAt,
                ProvenanceAcceptedAtVersion = relationship.Provenance?.AcceptedAtVersion,
                ProvenanceAcceptedOnBranch = relationship.Provenance?.AcceptedOnBranch,
                StaleSinceRevision = relationship.Stale?.SinceRevision,
                StaleReason = relationship.Stale?.Reason,
            });
        }

        foreach (var message in forked.Messages)
        {
            db.Messages.Add(new MessageEntity
            {
                Id = message.Id,
                ProjectId = projectId,
                Branch = branch,
                Name = message.Name,
                Description = message.Description,
                ProducerId = message.ProducerId,
                ConsumerIds = message.ConsumerIds.ToGuids(),
                SchemaId = message.SchemaId,
                Tags = message.Tags,
                FactStatus = message.FactStatus.ToApiString(),
                ProvenanceSource = message.Provenance?.Source,
                ProvenanceRevision = message.Provenance?.Revision,
                ProvenanceConfidence = message.Provenance?.Confidence,
                ProvenanceAcceptedBy = message.Provenance?.AcceptedBy,
                ProvenanceAcceptedAt = message.Provenance?.AcceptedAt,
                ProvenanceAcceptedAtVersion = message.Provenance?.AcceptedAtVersion,
                ProvenanceAcceptedOnBranch = message.Provenance?.AcceptedOnBranch,
                StaleSinceRevision = message.Stale?.SinceRevision,
                StaleReason = message.Stale?.Reason,
            });
        }
    }
}

/// <summary>Model streams as the rest of the app writes them.</summary>
public static class ModelStore
{
    public const string ProjectHeader = "project-id";

    /// <summary>A store whose appends carry the project that the model read model files rows under.</summary>
    public static IEventStore ForProject(this IEventStore store, Guid projectId) =>
        store.WithMetadata(metadata => metadata with
        {
            Headers = new Dictionary<string, string>(metadata.Headers) { [ProjectHeader] = projectId.ToString() },
        });

    /// <summary>The fork event that starts a draft or variant stream from a pinned state.</summary>
    public static ModelEvent.BranchForked Fork(ModelState source, Guid sourceStreamId, long sourceVersion) => new(
        sourceStreamId, sourceVersion,
        [.. source.Elements.Values], [.. source.Relationships.Values], [.. source.Messages.Values],
        [.. source.Operations.Values], [.. source.Channels.Values], [.. source.Subscriptions.Values],
        [.. source.Bindings.Values]);
}
