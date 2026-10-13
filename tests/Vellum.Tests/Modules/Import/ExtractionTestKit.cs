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
