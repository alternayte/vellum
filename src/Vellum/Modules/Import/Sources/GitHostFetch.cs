using Vellum.Modules.GitHosts;
using Vellum.Modules.GitHub;
using Vellum.Modules.Import.Scans;

namespace Vellum.Modules.Import.Sources;

/// <summary>
/// The fetch stage of a <c>gitlab</c>, <c>bitbucket</c> or <c>azure-devops</c> Source, through
/// the host's REST adapter (<see cref="GitHostReader"/>). The steps are those of a GitHub scan:
/// pin the commit once, list its files, fetch each file in scope. A resumed scan keeps the
/// commit it pinned. A host that names no blob SHA in its list (Bitbucket) gets the SHA computed
/// from the bytes, so the Snapshot has the same manifest as any other read of the same tree.
/// </summary>
internal static class GitHostFetch
{
    public static GitHostInfo HostOf(SourceKind kind) => kind switch
    {
        SourceKind.Gitlab => GitHostInfo.Gitlab,
        SourceKind.Bitbucket => GitHostInfo.Bitbucket,
        SourceKind.AzureDevops => GitHostInfo.AzureDevops,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ScanFailures.HostWords Words(GitHostInfo host) => new(host.Display, host.SecretName, host.SettingsField);

    public static async Task RunAsync(FetchContext context, CancellationToken ct)
    {
        var (scan, source) = (context.Scan, context.Source);
        var host = HostOf(source.Kind);
        var words = Words(host);
        var connected = await context.Services.GetRequiredService<GitHostReader>().ConnectAsync(host, scan.WorkspaceId, ct);
        if (connected.Failure is { } notConnected)
            throw ScanStop.Failed(ScanFailures.NotConnected(notConnected, words));
        var connection = connected.Value!;
        var locator = scan.Repository;

        // Step 1: pin the commit.
        if (scan.CommitSha is null)
        {
            var repository = Read(await connection.GetRepositoryAsync(locator, ct), connection, words, "repository", null);
            var reference = scan.RequestedRef ?? repository.DefaultBranch;
            scan.CommitSha = Read(await connection.ResolveRefAsync(locator, reference, ct), connection, words, "ref", scan.RequestedRef);
            scan.ResolvedRef = scan.IsBareCommit() ? null : reference;
            scan.RepositoryIsPrivate = repository.IsPrivate;
            scan.RepositoryName = repository.Name;
            await context.SaveAsync(ct);
        }

        // Step 2: the file list of that commit.
        if (!scan.TreeListed)
        {
            var files = await connection.ListFilesAsync(locator, scan.CommitSha, ct);
            if (files.Failure is GitHubFailure.Truncated)
            {
                scan.Partial = true;
                scan.Notes = ScanNotes.Write(ScanNotes.Read(scan.Notes).Append(ScanNotes.Of("tree-truncated", "")));
                await context.SaveAsync(ct);
                throw ScanStop.Failed(ScanFailures.Describe(new GitHubFailure.Truncated(), connection.HasToken, "tree", null, words));
            }
            await context.ListAsync(Read(files, connection, words, "tree", null), ct);
        }

        await context.RequeueLostAsync(ct);

        // Step 3: the files, in path order. One whose blob is stored already is not asked for.
        foreach (var file in await context.PendingAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!await context.UseStoredAsync(file, ct))
            {
                var read = await connection.GetFileAsync(locator, scan.CommitSha, file.Path, file.BlobSha, context.Limits.MaxFileBytes, ct);
                switch (read.Failure)
                {
                    case null:
                        await context.AcceptAsync(file, read.Value!, ct);
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
                        throw ScanStop.Failed(ScanFailures.Describe(read.Failure, connection.HasToken, "blob", null, words));
                }
            }
            await context.SaveAsync(ct);
        }
    }

    private static T Read<T>(GitHubResult<T> result, IGitHostConnection connection, ScanFailures.HostWords words, string stage, string? reference)
    {
        if (result.Failure is GitHubFailure.RateLimited limited)
            throw ScanStop.Waiting(limited.ResetAt ?? DateTimeOffset.MinValue);
        if (result.Failure is { } failure)
            throw ScanStop.Failed(ScanFailures.Describe(failure, connection.HasToken, stage, reference, words));
        return result.Value!;
    }
}
