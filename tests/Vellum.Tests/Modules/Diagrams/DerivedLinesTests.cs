using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Vellum.Modules.Diagrams;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Views;

namespace Vellum.Tests.Modules.Diagrams;

/// <summary>
/// Derived lines: the rules pinned by the fixture that the web test reads too, so the canvas and
/// the exports draw the same lines, and each export of the top level of that model shows them.
/// </summary>
public class DerivedLinesTests
{
    private sealed record FixtureElement(Guid Id, string Name, Guid? ParentId);
    private sealed record FixtureRelationship(Guid Id, Guid FromId, Guid ToId, string? Label);
    private sealed record FixtureLine(Guid FromId, Guid ToId, Guid[] RelationshipIds, string? Label);
    private sealed record FixtureCase(string Name, Guid[] Visible, Guid[] Direct, FixtureLine[] Derived);
    private sealed record Fixture(FixtureElement[] Elements, FixtureRelationship[] Relationships, FixtureCase[] Cases);

    private static readonly Fixture Data = JsonSerializer.Deserialize<Fixture>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Diagrams", "derived-lines.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static ElementKind KindOf(FixtureElement e) =>
        e.Name == "Customer" ? ElementKind.Actor
        : e.ParentId is null ? ElementKind.System
        : e.Name.Contains("component", StringComparison.Ordinal) ? ElementKind.Component
        : ElementKind.App;

    private static PinnedModel Model() =>
        new(new ModelPin(Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, null, null),
            ModelState.Initial with
            {
                Elements = Data.Elements.Select(e => new ElementState(
                        e.Id, KindOf(e), e.Name, null, null, null, ElementStatus.Current,
                        e.ParentId is { } p ? new ElementId(p) : null, [], null))
                    .ToImmutableDictionary(e => e.Id),
                Relationships = Data.Relationships.Select(r => new RelationshipState(r.Id, r.FromId, r.ToId, r.Label, null, null, null))
                    .ToImmutableDictionary(r => r.Id),
            });

    public static TheoryData<string> Cases() => [.. Data.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_fixture_cases_project_as_the_fixture_says(string name)
    {
        var c = Data.Cases.Single(c => c.Name == name);
        var state = Model().State;
        var (direct, derived) = DerivedLines.Project(state.Elements, state.Relationships.Values, c.Visible.Select(v => new ElementId(v)).ToHashSet());

        Assert.Equal(c.Direct.Order(), direct.Select(r => r.Id.Value).Order());
        Assert.Equal(
            c.Derived.Select(d => (d.FromId, d.ToId, string.Join(',', d.RelationshipIds), d.Label)),
            derived.Select(d => (d.FromId.Value, d.ToId.Value, string.Join(',', d.Relationships.Select(r => r.Id.Value)), d.Label)));
    }

    private static readonly ViewEntity TopLevel = new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Landscape", RootElementId = null };

    [Fact]
    public void The_svg_draws_each_derived_line_with_its_count_badge()
    {
        var diagram = ViewDiagram.Project(TopLevel, [], Model());
        var svg = XDocument.Parse(DiagramSvg.Write(diagram));

        // One line per direction: A to B (3), B to A, B to Customer, Customer to A; the direct "depends on" stays itself.
        Assert.Equal(4, svg.Descendants().Count(e => e.Name.LocalName == "path" && (string?)e.Attribute("class") == "derived-line"));
        Assert.Single(svg.Descendants(), e => e.Name.LocalName == "path" && (string?)e.Attribute("class") == "relationship");
        var labels = svg.Descendants().Where(e => (string?)e.Attribute("class") == "derived-line-label")
            .Select(g => string.Join('|', g.Elements().Where(t => t.Name.LocalName == "text").Select(t => t.Value)))
            .ToList();
        Assert.Equal(["3|3 relationships", "1|replies", "1", "1|places orders"], labels);
        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "marker" && (string?)e.Attribute("id") == "vellum-arrow-derived");
    }

    [Fact]
    public void Mermaid_writes_each_derived_line_as_a_dotted_link()
    {
        var text = MermaidView.Write(ViewDiagram.Project(TopLevel, [], Model()));

        Assert.Contains("    system_a -->|\"depends on\"| system_b\n", text);
        Assert.Contains("    system_a -.->|\"3 relationships\"| system_b\n", text);
        Assert.Contains("    system_b -.->|\"replies\"| system_a\n", text);
        Assert.Contains("    system_b -.-> customer\n", text);
        Assert.Contains("    customer -.->|\"places orders\"| system_a\n", text);
        Assert.Contains("%% A dotted link (-.->) is a derived line", text);
    }

    [Fact]
    public void Structurizr_writes_each_derived_line_once_with_the_derived_tag()
    {
        var inside = new ViewEntity { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = "Inside A", RootElementId = Data.Elements[0].Id };
        var text = StructurizrDsl.Write("P", Model(), [TopLevel, inside], new Dictionary<Guid, IReadOnlyList<LayoutPositionEntity>>());

        Assert.Contains("        system_a -> system_b \"3 relationships\" \"\" \"Derived\"\n", text);
        Assert.Contains("        customer -> system_a \"places orders\" \"\" \"Derived\"\n", text);
        // Inside A, a component of app 1 writes to app 2: one line from app 1, beside the direct "uses".
        Assert.Contains("        a_app_1 -> a_app_2 \"writes\" \"\" \"Derived\"\n", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "system_a -> system_b \"3 relationships\""));
        Assert.Contains("relationship \"Derived\" {", text);
    }
}
