using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vellum.Modules.Scoring;

public sealed record RubricCriterion(string Key, string Name, string Description, int Weight);

public sealed record Rubric(string Name, string DocType, IReadOnlyList<RubricCriterion> Criteria, string Prompt);

public class RubricService
{
    private readonly Dictionary<string, Rubric> _rubrics = new(StringComparer.OrdinalIgnoreCase);

    public RubricService(IWebHostEnvironment env)
    {
        var rubricDir = Path.Combine(env.ContentRootPath, "..", "Vellum.Web", "src", "rubrics");
        if (!Directory.Exists(rubricDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        foreach (var file in Directory.GetFiles(rubricDir, "*.md"))
        {
            var raw = File.ReadAllText(file);
            var rubric = Parse(raw, deserializer);
            if (rubric is not null)
                _rubrics[rubric.DocType] = rubric;
        }
    }

    public Rubric? GetRubric(string docType) =>
        _rubrics.TryGetValue(docType, out var rubric) ? rubric : null;

    public bool HasRubric(string docType) => _rubrics.ContainsKey(docType);

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
