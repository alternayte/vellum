using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>Loads the fixture repositories under <c>Fixtures/Repos</c> and builds small inline inputs.</summary>
internal static class Repo
{
    public const string Sha = "4f2a9c1e7b3d5a6088c2e1f04b7d9a3c5e6f7081";

    public static string Root(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Repos", name);

    public static ExtractionInput Load(string name)
    {
        var root = Root(name);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(path => new SourceFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        return new ExtractionInput($"github.com/acme/{name}", Sha, files);
    }

    public static ExtractionInput Of(params (string Path, string Content)[] files) =>
        new("github.com/acme/sample", Sha, files.Select(f => new SourceFile(f.Path, f.Content)).ToList());

    public static ExtractionResult Extract(params (string Path, string Content)[] files) => EvidenceExtractor.Extract(Of(files));

    /// <summary>
    /// Extracts one hostile file (with the given sound files beside it) and holds the extractors
    /// to their bounds. The operation count is the bound that no machine changes; the time and
    /// the allocation show that the count is honest.
    /// </summary>
    public static (ExtractionResult Result, long Work) ExtractBounded(string path, string content, params (string Path, string Content)[] others)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        Assert.True(bytes <= ExtractionLimits.Default.MaxFileBytes, $"{path} is over the file bound and would be refused unread");

        var input = Of([(path, content), .. others]);
        // Once before the measurement, so that the JIT is not what is measured.
        EvidenceExtractor.Extract(Of((path, content.Length > 2048 ? content[..2048] : content)));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = EvidenceExtractor.Extract(input, null, out var work);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var spent = work.GetValueOrDefault(path);

        Assert.DoesNotContain(result.Notes, n =>
            n.Reason.StartsWith("extractor-failure", StringComparison.Ordinal) || n.Reason.StartsWith("out-of-memory", StringComparison.Ordinal));
        // The budget is 64 operations for each byte of the largest file. One charge can step over it by its own size.
        Assert.True(spent <= 2 * ExtractionLimits.Default.MaxWorkPerFile, $"{path}: {spent} operations");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{path}: {watch.Elapsed.TotalMilliseconds:F0} ms for {bytes} bytes ({spent} operations)");
        Assert.True(allocated < 256L * 1024 * 1024, $"{path}: {allocated / (1024 * 1024)} MiB allocated for {bytes} bytes");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(ExtractionJson.Serialize(result)) < 4 * 1024 * 1024, $"{path}: output too large");
        return (result, spent);
    }

    public static ExtractedFact Fact(this ExtractionResult result, string key) =>
        result.Facts.SingleOrDefault(f => f.Key == key)
        ?? throw new Xunit.Sdk.XunitException($"No fact '{key}'. Facts:\n{string.Join("\n", result.Facts.Select(f => f.Key))}");

    public static bool Has(this ExtractionResult result, string key) => result.Facts.Any(f => f.Key == key);

    /// <summary>"path:start-end" of every evidence of a fact.</summary>
    public static string[] Lines(this ExtractedFact fact) =>
        fact.Evidence.Select(e => $"{e.Path}:{e.StartLine}-{e.EndLine}").ToArray();

    public static string? Attr(this ExtractedFact fact, string name) => fact.Attributes.GetValueOrDefault(name);

    public static string[] Reasons(this ExtractionResult result) => result.Notes.Select(n => n.Reason).ToArray();

    /// <summary>The text of the given lines of a file of the input, so a test can say what a reader sees there.</summary>
    public static string Text(this ExtractionInput input, string path, int start, int end) =>
        string.Join("\n", input.Files.Single(f => f.Path == path).Content.ReplaceLineEndings("\n").Split('\n')[(start - 1)..end]);
}
