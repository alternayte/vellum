using Deedbox;
using Microsoft.EntityFrameworkCore;
using Vellum.Modules.Modelling.Model;
using Vellum.Modules.Promises;
using Vellum.Modules.Schemas;
using Vellum.Modules.Trace.Engine;
using Vellum.Modules.Trace.Scenarios;
using Vellum.Modules.Workspaces;

namespace Vellum.Modules.Build;

public sealed record BuildSchema(Guid Id, string Name, string? Description, string Content, int Version);

/// <summary>
/// Everything an export is made from. The model is pinned: a named revision, or a branch
/// stream at one version. Schemas, scenarios and promises are not part of a model stream, so
/// they are read as they are at the export. The same input gives the same files, byte for
/// byte, whatever the order of its lists.
/// </summary>
public sealed record BuildInput(
    Guid ProjectId,
    string ProjectName,
    ModelPin Pin,
    ModelState Model,
    IReadOnlyList<BuildSchema> Schemas,
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<PromiseDto> Promises)
{
    /// <summary>Reads the input of a project for a pinned model.</summary>
    /// <exception cref="BundleTooLargeException">The scenarios alone are larger than a bundle may be; reading stops there.</exception>
    public static async Task<BuildInput> LoadAsync(
        IServiceProvider services, Guid projectId, PinnedModel pinned, BuildLimits limits, CancellationToken ct)
    {
        var projectName = await services.GetRequiredService<WorkspacesDbContext>().Projects.AsNoTracking()
            .Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);

        var schemas = await services.GetRequiredService<SchemasDbContext>().Schemas.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .Select(s => new BuildSchema(s.Id, s.Name, s.Description, s.Content, s.Version))
            .ToListAsync(ct);

        var store = services.GetRequiredService<IEventStore>();
        var scenarioIds = await services.GetRequiredService<ScenariosDbContext>().Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId).Select(s => s.Id).ToListAsync(ct);
        var scenarios = new List<Scenario>(scenarioIds.Count);
        long scenarioBytes = 0;
        foreach (var id in scenarioIds)
        {
            if ((await ScenarioStreams.LoadAsync(store, id, ct)).DocumentIn(projectId) is not { } document)
                continue;
            scenarios.Add(document);

            // Every scenario becomes a file of at least this size, so a project whose scenarios
            // cannot fit is refused before the rest is read into memory.
            scenarioBytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, BuildText.Json).Length;
            if (scenarioBytes > limits.MaxBundleBytes)
            {
                throw new BundleTooLargeException(
                    $"The scenarios of the project are larger than a bundle may be ({limits.MaxBundleBytes} bytes). Export a build brief for a part of the model.");
            }
        }

        // Promises are read against the pinned model: their links and the age of their evidence
        // then belong to the state the export names, and the export does not change when the
        // main model moves on.
        var promises = await services.GetRequiredService<PromiseReader>().AllAsync(projectId, ct, pinned);
        return new BuildInput(projectId, projectName, pinned.Pin, pinned.State, schemas, scenarios, promises);
    }
}
