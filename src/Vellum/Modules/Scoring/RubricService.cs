using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vellum.Modules.Scoring;

public sealed record RubricCriterion(string Key, string Name, string Description, int Weight);

public sealed record Rubric(string Name, string DocType, IReadOnlyList<RubricCriterion> Criteria, string Prompt);

public class RubricService
{
    private readonly Dictionary<string, Rubric> _rubrics = new(StringComparer.OrdinalIgnoreCase);

    public RubricService()
    {
        var assembly = typeof(RubricService).Assembly;
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            var raw = reader.ReadToEnd();
            var rubric = Parse(raw, deserializer);
            if (rubric is not null)
                _rubrics[rubric.DocType] = rubric;
        }
    }

    public Rubric? GetRubric(string docType) =>
        _rubrics.TryGetValue(docType, out var rubric) ? rubric : null;

    private static Rubric? Parse(string raw, IDeserializer deserializer)
    {
        var parts = raw.Split("---", 3, StringSplitOptions.None);
        if (parts.Length < 3) return null;

        var yamlBlock = parts[1].Trim();
        var prompt = parts[2].Trim();

        var frontmatter = deserializer.Deserialize<RubricFrontmatter>(yamlBlock);
        if (frontmatter?.DocType is null) return null;

        var criteria = frontmatter.Criteria?.Select(c =>
            new RubricCriterion(c.Key, c.Name, c.Description, c.Weight)).ToList()
            ?? [];

        return new Rubric(frontmatter.Name ?? "", frontmatter.DocType, criteria, prompt);
    }

    private class RubricFrontmatter
    {
        public string? Name { get; set; }
        public string? DocType { get; set; }
        public List<RubricCriterionYaml>? Criteria { get; set; }
    }

    private class RubricCriterionYaml
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int Weight { get; set; } = 1;
    }
}
