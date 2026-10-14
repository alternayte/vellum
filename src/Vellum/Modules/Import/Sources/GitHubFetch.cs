using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Scans;
using Vellum.Modules.Settings;
using Vellum.Shared;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The fetch stage of a <c>github</c> Source, through the Refit client of the workspace
/// (<see cref="GitHubReader"/>). A resumed scan keeps the commit it pinned, so a branch that
/// moved in the meantime cannot give it files of two revisions.
/// </summary>
internal static class GitHubFetch
{
    public static async Task RunAsync(FetchContext context, CancellationToken ct)
    {
        var (scan, db) = (context.Scan, context.Db);
        var connected = await context.Services.GetRequiredService<GitHubReader>().ConnectAsync(scan.WorkspaceId, ct);
        if (connected.Failure is { } notConnected)
            throw ScanStop.Failed(ScanFailures.NotConnected(notConnected));
        var github = connected.Value!;

        // Step 1: pin the commit.
        if (scan.CommitSha is null)
        {
            var asked = scan.Repository.Split('/');
            var repository = Read(await github.GetRepositoryAsync(asked[0], asked[1], ct), github, "repository", null);
            if (scan.PullRequestNumber is { } number)
            {
                var pull = await PinPullRequestAsync(github, asked[0], asked[1], repository, number, ct);
                scan.CommitSha = pull.HeadSha;
                scan.ResolvedRef = null;
                scan.PullRequest = pull.ToJson();
                var pullNotes = ScanNotes.Read(scan.Notes);
                if (pull.FromFork)
                    pullNotes.Add(ScanNotes.Of("pull-request-fork", ""));
                if (pull.Merged)
                    pullNotes.Add(ScanNotes.Of("pull-request-merged", ""));
                else if (pull.State == "closed")
                    pullNotes.Add(ScanNotes.Of("pull-request-closed", ""));
                scan.Notes = ScanNotes.Write(pullNotes);
            }
            else
            {
                var requested = scan.RequestedRef;
                if (requested is null)
                {
                    var settings = await context.Services.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(scan.WorkspaceId, ct);
                    requested = settings.Effective.Github.DefaultRef is { Length: > 0 } configured ? configured : null;
                }
                var reference = requested ?? repository.DefaultBranch;
                scan.CommitSha = Read(await github.ResolveRefAsync(asked[0], asked[1], reference, ct), github, "ref", requested);
                scan.ResolvedRef = scan.IsBareCommit() ? null : reference;
            }
            scan.RepositoryIsPrivate = repository.IsPrivate;
            scan.RepositoryId = repository.Id;
            scan.RepositoryName = repository.FullName;
            await IdentifyRepositoryAsync(db, scan, context.Source, repository, ct);
            await context.SaveAsync(ct);
        }
        var names = scan.Repository.Split('/');
        var (owner, name) = (names[0], names[1]);

        // Step 2: the file list of that commit, with the blob SHA of each file.
        if (!scan.TreeListed)
        {
            var tree = await github.GetTreeAsync(owner, name, scan.CommitSha, ct);
            if (tree.Failure is GitHubFailure.Truncated)
            {
                scan.Partial = true;
                scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of("tree-truncated", "")));
                await context.SaveAsync(ct);
                throw ScanStop.Failed(ScanFailures.TreeTruncated);
            }
            await context.ListAsync(Read(tree, github, "tree", null).Entries, ct);
        }

        await context.RequeueLostAsync(ct);

        // Step 3: the blobs, in path order. One that is stored already is not asked for.
        foreach (var file in await context.PendingAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!await context.UseStoredAsync(file, ct))
            {
                var blob = await github.GetBlobAsync(owner, name, file.BlobSha, context.Limits.MaxFileBytes, ct);
                switch (blob.Failure)
                {
                    case null:
                        await context.AcceptAsync(file, blob.Value!.Content, ct);
                        break;
                    case GitHubFailure.RateLimited limited:
                        throw ScanStop.Waiting(context.RetryAt(limited.ResetAt));
                    case GitHubFailure.TooLarge:
                        context.Skip(file, "limit:file-bytes", partial: true);
                        break;
                    case GitHubFailure.NotFound:
                        context.Skip(file, "blob-missing", partial: true);
                        break;
                    default:
                        throw ScanStop.Failed(ScanFailures.Describe(blob.Failure, github.HasToken, "blob", null));
                }
            }
            await context.SaveAsync(ct);
        }
    }

    /// <summary>
    /// GitHub's ID says which repository this is; its name can change. The scan, the Source
    /// and every row of the repository in this project take the name GitHub gives now, so a
    /// renamed repository keeps its accepted facts. Rows written before IDs were recorded are
    /// claimed by the name they have.
    /// </summary>
    private static async Task IdentifyRepositoryAsync(
        AppDbContext db, ScanEntity scan, SourceEntity source, GitHubRepository repository, CancellationToken ct)
    {
        var canonical = repository.FullName.ToLowerInvariant();
        // Without an ID nothing says that another name is the same repository.
        if (repository.Id is null || !ScanRequests.IsRepositoryName(canonical))
            canonical = scan.Repository;
        var (projectId, host, asked) = (scan.ProjectId, scan.Host, scan.Repository);

        if (repository.Id is { } id)
        {
            foreach (var known in new[] { asked, canonical }.Distinct())
            {
                await db.FactLinks.Where(l => l.ProjectId == projectId && l.Host == host && l.Repository == known && l.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(l => l.RepositoryId, id), ct);
                await db.Rejections.Where(r => r.ProjectId == projectId && r.Host == host && r.Repository == known && r.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.RepositoryId, id), ct);
                await db.TrackedRefs.Where(t => t.ProjectId == projectId && t.Host == host && t.Repository == known && t.RepositoryId == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(t => t.RepositoryId, id), ct);
            }

            // A row moves to the new name unless the new name already has that row.
            await db.FactLinks
                .Where(l => l.ProjectId == projectId && l.Host == host && l.RepositoryId == id && l.Repository != canonical
                    && !db.FactLinks.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical
                        && o.Subject == l.Subject && o.FactKeyHash == l.FactKeyHash))
                .ExecuteUpdateAsync(set => set.SetProperty(l => l.Repository, canonical), ct);
            await db.Rejections
                .Where(r => r.ProjectId == projectId && r.Host == host && r.RepositoryId == id && r.Repository != canonical
                    && !db.Rejections.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical
                        && o.Subject == r.Subject && o.FactKeyHash == r.FactKeyHash && o.Fingerprint == r.Fingerprint))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Repository, canonical), ct);
            await db.TrackedRefs
                .Where(t => t.ProjectId == projectId && t.Host == host && t.RepositoryId == id && t.Repository != canonical
                    && !db.TrackedRefs.Any(o => o.ProjectId == projectId && o.Host == host && o.Repository == canonical))
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Repository, canonical), ct);
        }

        // Links accepted before refs were recorded follow the default ref. This scan names it.
        if (scan.RequestedRef is null && scan.ResolvedRef is { } resolved)
        {
            await db.TrackedRefs
                .Where(t => t.ProjectId == projectId && t.Host == host && t.Repository == canonical && t.RefName == "")
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.RefName, resolved), ct);
        }

        if (canonical == asked)
            return;
        scan.Repository = canonical;
        source.Locator = canonical;
        // A name the person gave the Source stays; the default name follows the repository.
        if (source.Name == asked)
            source.Name = canonical;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The index that allows one active scan per repository, or one Source per
            // repository in a project, refused the new name.
            throw ScanStop.Failed(ScanFailures.RepositoryRenamed);
        }
    }

    /// <summary>
    /// Reads the pull request and returns its provenance. The scan reads the head commit
    /// through the repository of the scan, as every other read of it does. For a pull request
    /// from another repository (a fork, or a fork that was deleted) that works only when
    /// GitHub serves the head commit here, which it does for the pull request refs it keeps.
    /// When it does not, the scan stops: Vellum reads no other repository.
    /// </summary>
    private static async Task<ScanPullRequest> PinPullRequestAsync(
        GitHubConnection github, string owner, string name, GitHubRepository repository, int number, CancellationToken ct)
    {
        var read = await github.GetPullRequestAsync(owner, name, number, ct);
        if (read.Failure is GitHubFailure.NotFound)
            throw ScanStop.Failed(ScanFailures.PullRequestNotFound(number, github.HasToken));
        var pull = Read(read, github, "pull-request", null);

        var fromFork = pull.HeadRepository is null || !string.Equals(pull.HeadRepository, repository.FullName, StringComparison.OrdinalIgnoreCase);
        if (fromFork)
        {
            var served = await github.ResolveRefAsync(owner, name, pull.HeadSha, ct);
            if (served.Failure is GitHubFailure.RateLimited)
                Read(served, github, "ref", null);
            if (served.Failure is not null || served.Value != pull.HeadSha)
                throw ScanStop.Failed(ScanFailures.PullRequestHeadUnreadable(number));
        }

        return new ScanPullRequest(
            pull.Number, pull.Title, pull.State, pull.Merged, pull.BaseRef, pull.BaseSha, pull.HeadRef, pull.HeadSha,
            pull.HeadRepository, fromFork);
    }

    private static T Read<T>(GitHubResult<T> result, GitHubConnection github, string stage, string? reference)
    {
        if (result.Failure is GitHubFailure.RateLimited limited)
            throw ScanStop.Waiting(limited.ResetAt ?? DateTimeOffset.MinValue);
        if (result.Failure is { } failure)
            throw ScanStop.Failed(ScanFailures.Describe(failure, github.HasToken, stage, reference));
        return result.Value!;
    }
}
