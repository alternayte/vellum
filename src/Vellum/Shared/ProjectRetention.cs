namespace Vellum.Shared;

/// <summary>A project whose row is deleted, with what a module needs to find its own data.</summary>
/// <param name="Id">The project ID.</param>
/// <param name="WorkspaceId">The workspace the project belonged to.</param>
/// <param name="ModelStreamId">The stream of the project's main model.</param>
/// <param name="ActorId">The user who deleted the project, for the audit entries a module writes.</param>
public sealed record DeletedProject(Guid Id, Guid WorkspaceId, Guid ModelStreamId, string ActorId);

/// <summary>
/// What a module keeps when a project is deleted: nothing. Each module that holds project data
/// registers one implementation. The project delete runs every registered one after the
/// project row is gone, so no request reaches the data while it goes: every endpoint resolves
/// the project first.
/// <para>
/// An implementation deletes the rows and the streams of its own module, and finds them in its
/// own tables or in the events of its own stream type. It is safe to run again. It deletes each
/// stream on its own with <c>IEventStore.DeleteStream</c>, which locks one stream at a time and
/// so keeps the lock rule of <see cref="SharedTransaction"/>. A stream goes before the rows that
/// name it, so a delete that is cut short leaves a row that names the stream for the next run.
/// </para>
/// </summary>
public interface IProjectRetention
{
    Task DeleteProjectAsync(DeletedProject project, CancellationToken ct);
}
