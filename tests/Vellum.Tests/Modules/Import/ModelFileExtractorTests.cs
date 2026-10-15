using Vellum.Modules.Diagrams;
using Vellum.Modules.Import.Extraction;
using Vellum.Modules.Import.Proposals;
using Vellum.Modules.Modelling;
using Vellum.Tests.Modules.Diagrams;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The model-file extractors: Structurizr DSL and JSON, Backstage catalogs and ADRs. They read
/// text only, follow and run nothing, and every fact they give is <c>declared</c>.
/// </summary>
public class ModelFileExtractorTests
{
    private static string Fixture(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ModelFiles", path));

    private static string Element(params string[] names) =>
        "structurizr:element:" + string.Join('/', names);

    [Fact]
    public void A_DSL_file_is_read_without_following_running_or_substituting_anything()
    {
        var dsl = Fixture("hostile.dsl");
        var result = Repo.Extract(("workspace.dsl", dsl));

        // Every directive and every URL gave a note at its line, and nothing more.
        var notes = result.Notes.Where(n => n.Path == "workspace.dsl").Select(n => (n.Reason, n.Line)).ToList();
        Assert.Contains(("structurizr:include-not-followed", 3), notes);
        Assert.Contains(("structurizr:url-not-followed", 3), notes);
        Assert.Contains(("structurizr:include-not-followed", 4), notes);
        Assert.Contains(("structurizr:constant-not-substituted", 5), notes);
        Assert.Contains(("structurizr:script-not-run", 8), notes);
        Assert.Contains(("structurizr:variable-not-substituted", 12), notes);
        Assert.Contains(("structurizr:url-not-followed", 15), notes);
        Assert.Contains(("structurizr:group-ignored", 19), notes);
        Assert.Contains(("structurizr:unresolved-identifier", 26), notes);
        Assert.Contains(("structurizr:plugin-not-run", 33), notes);
        Assert.All(notes, n => Assert.StartsWith("structurizr:", n.Reason, StringComparison.Ordinal));
        Assert.True(result.IsComplete);

        // The script did not run: its person is not there. ${HOME} stays as written.
        Assert.DoesNotContain(result.Facts, f => f.Name == "Injected");
        Assert.Equal("Lives in ${HOME}", result.Fact(Element("Customer")).Attr("description"));

        var elements = result.Facts.Where(f => f.Kind == FactKind.Element).ToDictionary(f => f.Key);
        Assert.Equal(
            [Element("Billing"), Element("Customer"), Element("Shop"), Element("Shop", "Orders API"), Element("Shop", "Orders DB")],
            elements.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("actor", null), (elements[Element("Customer")].Attr("elementKind"), elements[Element("Customer")].Attr("parent")));
        Assert.Equal(("app", Element("Shop"), "Go"), (elements[Element("Shop", "Orders API")].Attr("elementKind"), elements[Element("Shop", "Orders API")].Attr("parent"), elements[Element("Shop", "Orders API")].Attr("technology")));
        Assert.Equal("store", elements[Element("Shop", "Orders DB")].Attr("elementKind"));
        // The group is gone; its system is a system at the top.
        Assert.Equal(("system", null), (elements[Element("Billing")].Attr("elementKind"), elements[Element("Billing")].Attr("parent")));
        Assert.Equal(["workspace.dsl:14-16"], elements[Element("Shop", "Orders API")].Lines());

        var relationships = result.Facts.Where(f => f.Kind == FactKind.Relationship)
            .Select(f => (f.Attr("from"), f.Attr("to"), f.Attr("label"), f.Attr("technology"), f.Attr("relationshipKind"))).ToList();
        Assert.Equal(3, relationships.Count);
        Assert.Contains((Element("Customer"), Element("Shop", "Orders API"), "places orders", "HTTPS", "structural"), relationships);
        Assert.Contains((Element("Shop", "Orders API"), Element("Billing"), "charges", "HTTPS", "behavioural"), relationships);
    }

    [Fact]
    public void The_Structurizr_DSL_that_Vellum_exports_reads_back_as_the_same_elements_and_relationships()
    {
        var model = DiagramExportTests.FixedModel();
        var dsl = StructurizrDsl.Write("Orders", model, [], new Dictionary<Guid, IReadOnlyList<Vellum.Modules.Views.LayoutPositionEntity>>());
        var result = Repo.Extract(("workspace.dsl", dsl));
        Assert.True(result.IsComplete);

        var state = model.State;
        string KeyOf(ElementState e) =>
            e.ParentId is { } parent ? KeyOf(state.Elements[parent]) + "/" + e.Name : "structurizr:element:" + e.Name;
        var expected = state.Elements.Values
            .Select(e => (KeyOf(e), (string?)e.Kind.ToString().ToLowerInvariant(), e.ParentId is { } p ? KeyOf(state.Elements[p]) : null,
                e.Kind is ElementKind.App or ElementKind.Store or ElementKind.Component ? e.Technology : null))
            .OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();
        var read = result.Facts.Where(f => f.Kind == FactKind.Element)
            .Select(f => (f.Key, f.Attr("elementKind"), f.Attr("parent"), f.Attr("technology")))
            .OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, read);

        var expectedRelationships = state.Relationships.Values
            .Select(r => (KeyOf(state.Elements[r.FromId]), KeyOf(state.Elements[r.ToId]), r.Label, r.Technology, r.Kind.ToString().ToLowerInvariant()))
            .OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
        var readRelationships = result.Facts.Where(f => f.Kind == FactKind.Relationship)
            .Select(f => (f.Attr("from")!, f.Attr("to")!, f.Attr("label"), f.Attr("technology"), f.Attr("relationshipKind")!))
            .OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(expectedRelationships, readRelationships);

        // The same workspace as Structurizr JSON gives the same keys.
        var json = Repo.Extract(("workspace.json", """
            {
              "name": "Orders",
              "model": {
                "people": [ { "id": "1", "name": "Customer", "relationships": [ { "id": "9", "sourceId": "1", "destinationId": "3", "description": "places orders" } ] } ],
                "softwareSystems": [ { "id": "2", "name": "Shop", "containers": [
                  { "id": "3", "name": "Orders API", "technology": "ASP.NET Core", "components": [ { "id": "6", "name": "Line parser" } ] },
                  { "id": "4", "name": "Orders DB", "technology": "PostgreSQL", "tags": "Element,Container,Database" } ] } ]
              }
            }
            """));
        Assert.Equal(
            [Element("Customer"), Element("Shop"), Element("Shop", "Orders API"), Element("Shop", "Orders API", "Line parser"), Element("Shop", "Orders DB")],
            json.Facts.Where(f => f.Kind == FactKind.Element).Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal("store", json.Fact(Element("Shop", "Orders DB")).Attr("elementKind"));
        Assert.True(json.Has(StructurizrExtractor.RelationshipKey(Element("Customer"), Element("Shop", "Orders API"), "places orders")));
    }

    [Fact]
    public void A_Backstage_catalog_maps_entity_refs_and_relations()
    {
        var result = Repo.Extract(("catalog-info.yaml", Fixture("catalog-info.yaml")));
        var elements = result.Facts.Where(f => f.Kind == FactKind.Element).ToDictionary(f => f.Key);

        Assert.Equal(
            ["api:default/orders-api", "api:default/payments-api", "component:default/orders", "component:default/payments", "domain:default/commerce", "resource:default/orders-db", "system:default/shop"],
            elements.Keys.Order(StringComparer.Ordinal));
        (string?, string?) KindAndParent(string key) => (elements[key].Attr("elementKind"), elements[key].Attr("parent"));
        Assert.Equal(("system", null), KindAndParent("system:default/shop"));
        Assert.Equal("domain:commerce", elements["system:default/shop"].Attr("tags"));
        Assert.Equal(("app", "system:default/shop"), KindAndParent("component:default/orders"));
        Assert.Equal(("store", "system:default/shop"), KindAndParent("resource:default/orders-db"));
        Assert.Equal("postgres", elements["resource:default/orders-db"].Attr("technology"));
        Assert.Equal(("component", "component:default/orders"), KindAndParent("api:default/orders-api"));
        Assert.Equal(("component", "component:default/payments"), KindAndParent("api:default/payments-api"));
        Assert.Equal("domain", elements["domain:default/commerce"].Attr("elementKind"));
        Assert.Equal(("Orders", "Takes orders from the web shop.", "go,orders"),
            (elements["component:default/orders"].Name, elements["component:default/orders"].Attr("description"), elements["component:default/orders"].Attr("tags")));
        Assert.Equal(("service", "production", "team-orders"),
            (elements["component:default/orders"].Attr("type"), elements["component:default/orders"].Attr("lifecycle"), elements["component:default/orders"].Attr("owner")));
        Assert.Equal(["catalog-info.yaml:17-33"], elements["component:default/orders"].Lines());

        var relationships = result.Facts.Where(f => f.Kind == FactKind.Relationship)
            .Select(f => (f.Attr("from"), f.Attr("to"), f.Attr("label"))).Order().ToList();
        Assert.Equal(
            [
                ("component:default/orders", "api:default/payments-api", "consumes"),
                ("component:default/orders", "component:default/payments", "depends on"),
                ("component:default/orders", "resource:default/orders-db", "depends on"),
            ],
            relationships);
        Assert.Contains(result.Notes, n => n.Reason == "backstage:location-not-followed");

        // The domain has no model home; everything else maps, with the entity ref as the key.
        var mapping = FactMapping.Map("model-file/catalog-info.yaml", result.Facts);
        Assert.Equal("element-kind-has-no-model-home", mapping.Facts.Single(f => f.Fact.Key == "domain:default/commerce").Reason);
        Assert.Contains(mapping.Candidates, c => c.FactKey == "component:default/orders" && c.EntityKind == EntityKinds.Element);
        Assert.Equal(3, mapping.Candidates.Count(c => c.EntityKind == EntityKinds.Relationship));
    }

    [Fact]
    public void Nygard_and_MADR_records_give_decisions_with_exact_line_citations()
    {
        var result = Repo.Extract(
            ("docs/adr/0001-record-architecture-decisions.md", Fixture("docs/adr/0001-record-architecture-decisions.md")),
            ("docs/decisions/0002-use-postgres.md", Fixture("docs/decisions/0002-use-postgres.md")),
            ("docs/adr/README.md", "# ADRs\n\nThe index."),
            ("README.md", "# Shop\n\n## Decision\n\nNot an ADR: it is not in an ADR folder."));

        var decisions = result.Facts.Where(f => f.Kind == FactKind.Decision).ToList();
        Assert.Equal(["adr:docs/adr/0001-record-architecture-decisions.md", "adr:docs/decisions/0002-use-postgres.md"], decisions.Select(d => d.Key));

        var nygard = decisions[0];
        Assert.Equal(["docs/adr/0001-record-architecture-decisions.md:1-19"], nygard.Lines());
        Assert.Equal("adr.nygard", nygard.Evidence[0].Rule);
        Assert.Equal(("Record architecture decisions", "Accepted", "2016-02-12"), (nygard.Name, nygard.Attr("status"), nygard.Attr("date")));
        Assert.Equal("We need to record the architectural decisions made on this project.", nygard.Attr("context"));
        Assert.Equal("We will use Architecture Decision Records, as described by Michael Nygard.", nygard.Attr("decision"));
        Assert.Equal("See Michael Nygard's article, linked above.", nygard.Attr("consequences"));
        Assert.Equal(("1-1", "5-7", "9-11", "13-15", "17-19"),
            (nygard.Attr("lines.title"), nygard.Attr("lines.status"), nygard.Attr("lines.context"), nygard.Attr("lines.decision"), nygard.Attr("lines.consequences")));

        var madr = decisions[1];
        Assert.Equal(["docs/decisions/0002-use-postgres.md:6-31"], madr.Lines());
        Assert.Equal("adr.madr", madr.Evidence[0].Rule);
        Assert.Equal(("Use PostgreSQL for orders", "accepted", "2023-05-04"), (madr.Name, madr.Attr("status"), madr.Attr("date")));
        Assert.Equal("Orders need transactions across order lines.\nWhich database do we use?", madr.Attr("context"));
        Assert.Equal("Chosen option: \"PostgreSQL\", because it gives transactions.", madr.Attr("decision"));
        Assert.Equal("* Good, because the outbox lives in the same transaction.\n* Bad, because we run one more server.", madr.Attr("consequences"));
        Assert.Equal(("8-11", "18-20", "22-25"), (madr.Attr("lines.context"), madr.Attr("lines.decision"), madr.Attr("lines.consequences")));
    }

    [Fact]
    public void A_DSL_file_of_control_marks_or_backslashes_stays_within_its_work_bound()
    {
        // A long run of U+0001 in a quoted name and in a bare word.
        var marks = new string('\u0001', 120_000);
        var (quoted, _) = Repo.ExtractBounded("a.dsl", $"workspace {{\nmodel {{\nperson \"{marks}\"\nperson {marks}\n}}\n}}\n");
        Assert.DoesNotContain(quoted.Facts, f => f.Name.Contains('\u0001'));

        // A long line of backslashes, each one a token.
        var (slashes, _) = Repo.ExtractBounded("b.dsl", "workspace {\nmodel {\n" + string.Concat(Enumerable.Repeat("\\ ", 110_000)) + "\n}\n}\n");
        Assert.True(slashes.Facts.Count == 0);
    }

    [Fact]
    public void Text_that_the_model_refuses_is_left_out_of_a_model_file_fact_with_a_note()
    {
        var longTag = new string('t', 150);
        var dsl = $$"""
            workspace {
                model {
                    s = softwareSystem "Shop" {
                        api = container "Orders API" "Takes orders" "{{new string('g', 300)}}" "web,{{longTag}},bad{{'\u0007'}}tag"
                    }
                    ghost = person "{{new string('n', 230)}}"
                }
            }
            """;
        var result = Repo.Extract(("workspace.dsl", dsl));
        var api = result.Fact(Element("Shop", "Orders API"));
        Assert.Null(api.Attr("technology"));
        Assert.Equal("web", api.Attr("tags"));
        Assert.DoesNotContain(result.Facts, f => f.Name.StartsWith("nnn", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Reason == "model-file:value-dropped:technology");
        Assert.Contains(result.Notes, n => n.Reason == "model-file:value-dropped:tag");
        Assert.Contains(result.Notes, n => n.Reason == "model-file:value-dropped:name");

        var catalog = Repo.Extract(("catalog-info.yaml", $"""
            apiVersion: backstage.io/v1alpha1
            kind: System
            metadata:
              name: shop
              tags: [ok, {longTag}]
            """));
        Assert.Equal("ok", catalog.Fact("system:default/shop").Attr("tags"));
    }

    [Fact]
    public void Model_file_facts_are_declared_and_code_facts_stay_observed()
    {
        var result = Repo.Extract(
            ("workspace.dsl", Fixture("hostile.dsl")),
            ("catalog-info.yaml", Fixture("catalog-info.yaml")),
            ("docs/adr/0001-record-architecture-decisions.md", Fixture("docs/adr/0001-record-architecture-decisions.md")),
            ("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n"));

        var mapping = FactMapping.Map("github.com/acme/sample", result.Facts);
        var fromModelFiles = mapping.Candidates.Where(c => c.FactKey.StartsWith("structurizr:", StringComparison.Ordinal)
            || c.FactKey.Contains(":default/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fromModelFiles);
        Assert.All(fromModelFiles, c => Assert.Equal(TruthLabels.Declared, c.FactStatus));
        Assert.All(mapping.Candidates.Except(fromModelFiles), c => Assert.Equal(TruthLabels.ObservedInSource, c.FactStatus));
        Assert.Contains(mapping.Candidates, c => c.FactStatus == TruthLabels.ObservedInSource);
        Assert.Equal("proposed-as-decision", mapping.Facts.Single(f => f.Fact.Kind == FactKind.Decision).Reason);
    }
}
