// src/Vellum/Modules/Drafts/Merge/MergeTypes.cs
namespace Vellum.Modules.Drafts.Merge;

public sealed record MergePreviewResult(
    IReadOnlyList<MergeChange> AutoResolved,
    IReadOnlyList<MergeConflict> Conflicts);

public sealed record MergeChange(
    string EntityType,
    Guid EntityId,
    string ChangeKind,
    object? ResolvedValue);

public sealed record MergeConflict(
    string EntityType,
    Guid EntityId,
    string ConflictKind,
    object? BaseValue,
    object? OursValue,
    object? TheirsValue);

public sealed record MergeResolution(Guid EntityId, string Resolution);
