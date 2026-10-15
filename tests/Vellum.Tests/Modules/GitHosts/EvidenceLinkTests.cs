using Vellum.Modules.Import;
using Vellum.Modules.Import.Proposals;

namespace Vellum.Tests.Modules.GitHosts;

/// <summary>
/// An evidence link opens the cited lines at the commit on the Source's own host. Each host
/// writes a file page and a line range its own way (GitHub, GitLab, Bitbucket Cloud and Azure
/// DevOps "link to a line"); a Source without a web page has no link.
/// </summary>
public class EvidenceLinkTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly EvidenceRef Range = new("src/Orders Api/Program.cs", 4, 9, "dotnet.route", 1);
    private static readonly EvidenceRef Line = new("src/a.cs", 7, 7, "dotnet.route", 1);

    [Theory]
    [InlineData(SourceKind.Github, "github.com/acme/orders",
        "https://github.com/acme/orders/blob/" + Sha + "/src/Orders%20Api/Program.cs#L4-L9",
        "https://github.com/acme/orders/blob/" + Sha + "/src/a.cs#L7")]
    [InlineData(SourceKind.Gitlab, "gitlab.com/acme/platform/orders",
        "https://gitlab.com/acme/platform/orders/-/blob/" + Sha + "/src/Orders%20Api/Program.cs#L4-9",
        "https://gitlab.com/acme/platform/orders/-/blob/" + Sha + "/src/a.cs#L7")]
    [InlineData(SourceKind.Bitbucket, "bitbucket.org/acme/orders",
        "https://bitbucket.org/acme/orders/src/" + Sha + "/src/Orders%20Api/Program.cs#lines-4:9",
        "https://bitbucket.org/acme/orders/src/" + Sha + "/src/a.cs#lines-7")]
    [InlineData(SourceKind.AzureDevops, "dev.azure.com/acme/shop platform/orders",
        "https://dev.azure.com/acme/shop%20platform/_git/orders?path=%2Fsrc%2FOrders%20Api%2FProgram.cs&version=GC" + Sha + "&line=4&lineEnd=10&lineStartColumn=1&lineEndColumn=1&lineStyle=plain",
        "https://dev.azure.com/acme/shop%20platform/_git/orders?path=%2Fsrc%2Fa.cs&version=GC" + Sha + "&line=7&lineEnd=8&lineStartColumn=1&lineEndColumn=1&lineStyle=plain")]
    public void A_link_has_the_page_and_line_form_of_its_host(SourceKind kind, string repository, string range, string line)
    {
        Assert.Equal(range, EvidenceLinks.Url(kind, repository, Sha, Range));
        Assert.Equal(line, EvidenceLinks.Url(kind, repository, Sha, Line));
    }

    [Theory]
    [InlineData(SourceKind.Directory, "directory/acme/orders")]
    [InlineData(SourceKind.Archive, "archive/orders")]
    [InlineData(SourceKind.AgentPush, "push/acme/orders")]
    [InlineData(SourceKind.ModelFile, "upload/workspace.dsl")]
    public void A_Source_without_a_web_page_has_no_link(SourceKind kind, string repository) =>
        Assert.Null(EvidenceLinks.Url(kind, repository, Sha, Range));

    [Fact]
    public void A_fact_cited_at_another_commit_links_that_commit() =>
        Assert.StartsWith("https://bitbucket.org/acme/orders/src/" + new string('f', 40) + "/",
            EvidenceLinks.Url(SourceKind.Bitbucket, "bitbucket.org/acme/orders", Sha, Line with { CommitSha = new string('f', 40) }), StringComparison.Ordinal);
}
