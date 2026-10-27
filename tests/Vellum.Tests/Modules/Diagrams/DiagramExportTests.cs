using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vellum.Modules.Diagrams;
using Vellum.Modules.Modelling;
using Vellum.Modules.Promises;
using Vellum.Modules.Views;

namespace Vellum.Tests.Modules.Diagrams;

/// <summary>
/// The diagram exports: the SVG of a fixed model and layout is byte-stable, hostile names stay
/// text in every format, Mermaid IDs are unique, and the Structurizr DSL of the Orders example
/// is well formed. The endpoint test proves that a pinned revision gives the same bytes after
/// main changes.
/// </summary>
public class DiagramExportTests
{
    private static readonly Guid Shop = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Customer = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Api = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Db = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid Worker = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static readonly Guid Parser = Guid.Parse("00000000-0000-0000-0000-00000000000f");

    private static ElementState Element(Guid id, ElementKind kind, string name, Guid? parent = null,
        string? technology = null, string? description = null, FactStatus fact = FactStatus.Declared, string? icon = null) =>
        new(id, kind, name, description, technology, null, ElementStatus.Current, parent is { } p ? new ElementId(p) : null, [], icon, fact);

    private static RelationshipState Relationship(int n, Guid from, Guid to, string? label, string? technology = null,
        string? lineShape = null, RelationshipKind kind = RelationshipKind.Structural) =>
        new(new Guid(n, 0, 0, new byte[8]), from, to, label, technology, null, lineShape, kind);

    private static PinnedModel Model(IEnumerable<ElementState> elements, IEnumerable<RelationshipState> relationships) =>
        new(new ModelPin(Guid.Parse("11111111-1111-1111-1111-111111111111"), 42, Guid.Parse("22222222-2222-2222-2222-222222222222"), "v1"),
            ModelState.Initial with
            {
                Elements = elements.ToImmutableDictionary(e => e.Id),
                Relationships = relationships.ToImmutableDictionary(r => r.Id),
            });

    internal static PinnedModel FixedModel() => Model(
        [
            Element(Shop, ElementKind.System, "Shop", technology: "Kubernetes"),
            Element(Customer, ElementKind.Actor, "Customer"),
            Element(Api, ElementKind.App, "Orders API", Shop, "ASP.NET Core", "Takes orders from the web shop and the mobile app, and reserves stock for each line.", FactStatus.ObservedInSource),
            Element(Db, ElementKind.Store, "Orders DB", Shop, "PostgreSQL"),
            Element(Worker, ElementKind.App, "Fulfilment worker", Shop, "Go", fact: FactStatus.Inferred, icon: "cog"),
            Element(Parser, ElementKind.Component, "Line parser", Api),
        ],
        [
            Relationship(1, Api, Db, "reads and writes", "SQL"),
            Relationship(2, Api, Worker, "publishes OrderPlaced", "RabbitMQ", "step", RelationshipKind.Behavioural),
            Relationship(3, Worker, Db, "updates status", lineShape: "straight"),
            Relationship(4, Worker, Api, "asks for lines"),
            Relationship(5, Customer, Api, "places orders"),
        ]);

    private static ViewEntity ShopView() => new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Shop containers", RootElementId = Shop };

    // The worker has no saved place: it stands on the fallback grid.
    private static LayoutPositionEntity[] ShopLayout() =>
    [
        new() { ElementId = Api, X = 0, Y = 0 },
        new() { ElementId = Db, X = 420.5, Y = 260.25 },
    ];

    [Fact]
    public void The_svg_of_a_fixed_model_and_layout_has_fixed_bytes()
    {
        var first = DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel()));
        var second = DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout().Reverse(), FixedModel()));
        Assert.Equal(first, second);

        // A change to the renderer that changes the picture changes this hash. Look at the new
        // picture, then update the hash on purpose.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first)));
        Assert.Equal("05c0051605ff29aed2ebfc94091d9e64f9e21a8efb0f2af8dab047be8726a61d", hash);

        // The worker has no saved place: it stands on the fallback grid, at the left edge of the
        // placed cards and 80 below the lowest (260.25 + 128 + 80).
        var svg = XDocument.Parse(first);
        Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("x") == "0" && (string?)e.Attribute("y") == "468.25");
    }

    [Fact]
    public void Saved_sides_attach_the_svg_path_and_enter_the_fingerprint_only_when_present()
    {
        var api = new RelationshipId(new Guid(1, 0, 0, new byte[8]));
        LayoutEdgeEntity[] routeOnly = [new() { RelationshipId = api.Value, RoutePoints = null }];
        LayoutEdgeEntity[] sided = [new() { RelationshipId = api.Value, SourceSide = "top", TargetSide = null }];

        // A view without a saved side keeps the fingerprint it had before sides existed.
        var plain = ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout());
        Assert.Equal(plain, ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout(), routeOnly));
        Assert.NotEqual(plain, ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout(), sided));

        // Auto: the Orders API (0,0, 264 wide) faces the Orders DB on its right side.
        var auto = DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel()));
        Assert.Contains("d=\"M264,64 C", auto);

        // Saved: the source end leaves the top of the card; the target end stays auto.
        var diagram = ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel(), sided);
        Assert.Equal(new RelationshipSides(CardSide.Top, null), diagram.Sides[api]);
        var svg = DiagramSvg.Write(diagram);
        Assert.Contains("d=\"M132,0 C", svg);
        Assert.DoesNotContain("d=\"M264,64 C", svg);
    }

    // The route Tidy saved for "reads and writes": from the right side of the Orders API (0,0) to
    // the left side of the Orders DB (420.5,260.25), around a corner, its label on the way down.
    private static System.Text.Json.JsonDocument ApiDbRoute() => System.Text.Json.JsonDocument.Parse("""
        {"from":{"x":0,"y":0,"width":264,"height":128},"to":{"x":420.5,"y":260.25,"width":264,"height":128},
         "points":[{"x":264,"y":64},{"x":300,"y":64},{"x":300,"y":324.25},{"x":420.5,"y":324.25}],"label":{"x":300,"y":200}}
        """);

    [Fact]
    public void Lines_a_view_hides_are_left_out_of_the_svg_mermaid_and_dsl_and_enter_the_fingerprint()
    {
        var readsAndWrites = new Guid(1, 0, 0, new byte[8]);
        var shown = DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel()));
        Assert.Contains("reads and writes", shown);
        Assert.Contains("publishes OrderPlaced", shown);

        // One relationship hidden by ID and every behavioural one by kind.
        var hidden = new LineVisibility(true, true, false, true, [readsAndWrites], []);
        var diagram = ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel(), visibility: hidden);
        var svg = DiagramSvg.Write(diagram);
        Assert.DoesNotContain("reads and writes", svg);
        Assert.DoesNotContain("publishes OrderPlaced", svg);
        Assert.Contains("updates status", svg);
        var mermaid = MermaidView.Write(diagram);
        Assert.DoesNotContain("reads and writes", mermaid);
        Assert.DoesNotContain("publishes OrderPlaced", mermaid);
        Assert.Contains("updates status", mermaid);

        // A view that hides nothing keeps the fingerprint it had before line visibility existed.
        var plain = ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout());
        Assert.Equal(plain, ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout(), visibility: LineVisibility.AllShown));
        Assert.NotEqual(plain, diagram.Layout);

        // The derived line from the customer to the shop, hidden by its pair of cards.
        var landscape = new ViewEntity { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = "Landscape" };
        Assert.Contains("places orders", DiagramSvg.Write(ViewDiagram.Project(landscape, [], FixedModel())));
        var pairHidden = new LineVisibility(true, true, true, true, [], [new HiddenDerivedLine(Customer, Shop)]);
        Assert.DoesNotContain("places orders", DiagramSvg.Write(ViewDiagram.Project(landscape, [], FixedModel(), visibility: pairHidden)));
        var noDerived = LineVisibility.AllShown with { DerivedLines = false };
        Assert.Empty(ViewDiagram.Project(landscape, [], FixedModel(), visibility: noDerived).DerivedLines);

        // The DSL keeps every relationship in the model and excludes the hidden ones from the view.
        var dsl = StructurizrDsl.Write("P", FixedModel(), [landscape, ShopView()], NoLayout,
            visibility: new Dictionary<Guid, LineVisibility> { [landscape.Id] = pairHidden, [ShopView().Id] = hidden });
        AssertWellFormedDsl(dsl);
        Assert.Contains("rel_orders_api_orders_db = orders_api -> orders_db \"reads and writes\"", dsl);
        Assert.Contains("rel_orders_api_fulfilment_worker = orders_api -> fulfilment_worker", dsl);
        Assert.Contains("exclude rel_orders_api_orders_db\n", dsl);
        Assert.Contains("exclude rel_orders_api_fulfilment_worker\n", dsl);
        Assert.DoesNotContain("exclude rel_fulfilment_worker", dsl);
        // The landscape view is the only one that draws the derived line, and it hides it.
        Assert.DoesNotContain("Derived", dsl);
        Assert.Contains("\"places orders\"", dsl);
    }

    [Fact]
    public void A_saved_route_draws_in_the_svg_while_its_cards_stay_and_enters_the_fingerprint()
    {
        var api = new RelationshipId(new Guid(1, 0, 0, new byte[8]));
        LayoutEdgeEntity[] routed = [new() { RelationshipId = api.Value, RoutePoints = ApiDbRoute() }];

        var plain = ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout());
        Assert.NotEqual(plain, ViewDiagram.LayoutFingerprint(ShopView(), ShopLayout(), routed));

        // The path runs through the points with rounded corners, and the label sits where the
        // layout made room for it, not in the middle of a curve.
        var svg = DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel(), routed));
        Assert.Contains("d=\"M264,64 L290,64 Q300,64 300,74 L300,314.25 Q300,324.25 310,324.25 L420.5,324.25\"", svg);
        Assert.DoesNotContain("d=\"M264,64 C", svg);
        Assert.Contains(">reads and writes</text>", svg);
        var label = XDocument.Parse(svg).Descendants().First(e => e.Name.LocalName == "text" && e.Value == "reads and writes");
        Assert.Equal("300", (string?)label.Attribute("x"));

        // The Orders DB moved: the route no longer fits, and the line takes its auto sides again.
        LayoutPositionEntity[] moved = [ShopLayout()[0], new() { ElementId = Db, X = 520, Y = 260.25 }];
        var fallback = DiagramSvg.Write(ViewDiagram.Project(ShopView(), moved, FixedModel(), routed));
        Assert.DoesNotContain("Q300,64 300,74", fallback);
        Assert.Contains("d=\"M264,64 C", fallback);

        // A saved side wins over the route.
        LayoutEdgeEntity[] sided = [new() { RelationshipId = api.Value, RoutePoints = ApiDbRoute(), SourceSide = "top" }];
        Assert.DoesNotContain("Q300,64 300,74", DiagramSvg.Write(ViewDiagram.Project(ShopView(), ShopLayout(), FixedModel(), sided)));
    }

    private const string Script = "<script>alert(1)</script>";
    private const string Quotes = "Say \"hi\" & 'bye'";
    private const string Lines = "first\nsecond\r\nthird";
    private const string Arrow = "a --> end; %%{init: {\"theme\":\"dark\"}}%%";

    private static PinnedModel HostileModel() => Model(
        [
            Element(Shop, ElementKind.System, Script, technology: Quotes),
            Element(Api, ElementKind.App, Quotes, Shop, Lines, Lines),
            Element(Db, ElementKind.Store, Arrow, Shop, "back\\slash"),
            Element(Worker, ElementKind.Component, Lines, Shop),
        ],
        [
            Relationship(1, Api, Db, Script, Arrow),
            Relationship(2, Db, Worker, Arrow, Quotes, kind: RelationshipKind.Behavioural),
        ]);

    private static ViewEntity HostileView() => new() { Id = Guid.NewGuid(), Name = Arrow + Script, RootElementId = Shop };

    [Fact]
    public void Hostile_names_stay_text_in_the_svg()
    {
        var text = DiagramSvg.Write(ViewDiagram.Project(HostileView(), [], HostileModel()));
        var svg = XDocument.Parse(text);

        Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "script");
        var shown = string.Join("|", svg.Descendants().Where(e => e.Name.LocalName is "text" or "tspan" or "title").Select(e => e.Value));
        Assert.Contains(Script, shown);
        Assert.Contains(Quotes, shown);
        Assert.Contains("first second third", shown);
        Assert.Contains("<script>", svg.Root!.Elements().First(e => e.Name.LocalName == "title").Value);
    }

    [Fact]
    public void Hostile_names_stay_inside_their_quotes_in_mermaid()
    {
        var text = MermaidView.Write(ViewDiagram.Project(HostileView(), [], HostileModel()));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Three comment lines, the header, a subgraph with three nodes and its end, two links.
        Assert.Equal(11, lines.Length);
        Assert.All(lines.Take(3), l => Assert.StartsWith("%% ", l));
        Assert.DoesNotContain("%%{", text);
        Assert.Equal("flowchart TB", lines[3]);
        var node = new Regex("""^ {8}[a-z][a-z0-9_]*(\(\[|\[\(|\[\[|\[)"[^"]*"(\]\)|\)\]|\]\]|\])$""");
        Assert.All(lines[5..8], l => Assert.Matches(node, l));
        var link = new Regex("""^ {4}[a-z][a-z0-9_]* (-->|==>)\|"[^"]*"\| [a-z][a-z0-9_]*$""");
        Assert.All(lines[^2..], l => Assert.Matches(link, l));
        Assert.Equal("    end", lines[8]);
        // The comment lines may hold a name as it is; a comment is not drawn.
        Assert.DoesNotContain("<script", string.Join('\n', lines.Skip(3)));
        Assert.Contains("#lt;script#gt;", text);
    }

    private static readonly Dictionary<Guid, IReadOnlyList<LayoutPositionEntity>> NoLayout = [];

    [Fact]
    public void Hostile_names_stay_inside_their_quotes_in_structurizr()
    {
        var text = StructurizrDsl.Write(Script + "\n}", HostileModel(), [HostileView()], NoLayout);
        AssertWellFormedDsl(text);
        Assert.Contains("\\\"hi\\\"", text);
        // Structurizr has no escape for a backslash: it stays single.
        Assert.Contains("\"back\\slash\"", text);
    }

    [Fact]
    public void Structurizr_strings_hold_no_substitution_no_closing_backslash_and_no_equal_sibling_names()
    {
        var model = Model(
            [
                Element(Shop, ElementKind.System, "Payments"),
                Element(Customer, ElementKind.System, "payments"),
                Element(Api, ElementKind.App, "${HOME} and $${PATH} and \\${USER}", Shop),
                Element(Db, ElementKind.App, "Share\\", Shop),
                Element(Worker, ElementKind.App, "Share\\", Shop),
                Element(Parser, ElementKind.App, "Payments", Customer),
            ],
            [Relationship(1, Api, Db, "a"), Relationship(2, Worker, Db, "a")]);
        var text = StructurizrDsl.Write("P", model, [], NoLayout);
        AssertWellFormedDsl(text);

        Assert.DoesNotContain("${", text, StringComparison.Ordinal);
        Assert.Contains("$\u200B{HOME}", text);
        Assert.Contains("\"Share\u29F5\"", text);
        Assert.Contains("\"Share\\ (2)\"", text);
        // People and software systems share one scope; a container of another system may repeat a name.
        Assert.Contains("\"payments (2)\"", text);
        Assert.Contains("= container \"Payments\"", text);
        Assert.Contains("// Renamed for unique names: ", text);
        Assert.Contains("!impliedRelationships false", text);
    }

    [Fact]
    public void Mermaid_ids_are_unique_and_plain()
    {
        var names = new[] { "Orders API", "orders-api", "Orders  API", "end", "123", "", "Ünïcode", "subgraph", "orders_api" };
        var elements = names.Select((name, i) => Element(new Guid(i + 1, 0, 0, new byte[8]), ElementKind.App, name)).ToList();
        var view = new ViewEntity { Id = Guid.NewGuid(), Name = "All" };
        var text = MermaidView.Write(ViewDiagram.Project(view, [], Model(elements, [])));

        var ids = text.Split('\n').Where(l => l.StartsWith("    ", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"^ +([^\[\( ]+)").Groups[1].Value).ToList();
        Assert.Equal(names.Length, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z][a-z0-9_]*$", id));
        Assert.DoesNotContain("end", ids);
        Assert.DoesNotContain("subgraph", ids);
    }

    [Fact]
    public void Many_equal_names_get_numbered_identifiers()
    {
        var items = Enumerable.Range(1, 3000).Select(i => (new Guid(i, 0, 0, new byte[8]), "Worker")).Append((Guid.NewGuid(), "worker_2"));
        var ids = DiagramText.Identifiers(items, "element", new HashSet<string>());
        Assert.Equal(3001, ids.Values.Distinct().Count());
        Assert.Contains("worker_3000", ids.Values);
        Assert.Contains("worker_2_2", ids.Values);
    }

    /// <summary>
    /// Braces balance outside strings, every string closes on its line, and every relationship
    /// and view names an identifier that the model declares.
    /// </summary>
    internal static void AssertWellFormedDsl(string text)
    {
        var depth = 0;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            var code = new StringBuilder();
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    // Structurizr knows one escape: a backslash before a double quote.
                    if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; code.Append("\"\""); continue; }
                if (c == '{') depth++;
                if (c == '}') depth--;
                Assert.True(depth >= 0, $"A brace closes before it opens: {raw}");
                code.Append(c);
            }
            Assert.False(inString, $"A string does not close on its line: {raw}");

            var codeText = code.ToString();
            if (Regex.Match(codeText, @"^([A-Za-z0-9_-]+) = (person|softwareSystem|container|component) ") is { Success: true } d)
                Assert.True(declared.Add(d.Groups[1].Value), $"Declared twice: {d.Groups[1].Value}");
            if (Regex.Match(codeText, @"^([A-Za-z0-9_-]+) -> ([A-Za-z0-9_-]+) ") is { Success: true } r)
                used.AddRange([r.Groups[1].Value, r.Groups[2].Value]);
            if (Regex.Match(codeText, @"^(container|component) ([A-Za-z0-9_-]+) """"") is { Success: true } v)
                used.Add(v.Groups[2].Value);
        }
        Assert.Equal(0, depth);
        Assert.NotEmpty(declared);
        Assert.All(used, id => Assert.Contains(id, declared));
    }
}

/// <summary>The export endpoints against a real database.</summary>
public class DiagramExportEndpointTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vellum.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Vellum.slnx was not found above the test directory.");
    }

    [Fact]
    public async Task The_orders_example_exports_and_a_pinned_revision_keeps_its_bytes()
    {
        var client = await TestHost.SignInAsync(fixture.Factory);
        var (workspaceId, projectId) = await TestHost.CreateProjectAsync(client);
        string Url(string path) => $"/api/projects/{projectId}/{path}";

        var model = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "examples", "orders", "model.json"));
        var imported = await client.PostAsync(Url("import"), new StringContent(model, Encoding.UTF8, "application/json"));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        var elements = JsonNode.Parse(model)!["elements"]!.AsArray();
        Guid Named(string name) => elements.First(e => e!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<Guid>();

        var landscape = Guid.NewGuid();
        var shop = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url("views"), new { id = landscape, name = "Landscape" }));
        await TestHost.Created(client.PostAsJsonAsync(Url("views"), new { id = shop, name = "Shop", rootElementId = Named("Shop") }));
        var saved = await client.PutAsJsonAsync(Url($"views/{shop}/layout"), new
        {
            positions = new[] { new { elementId = Named("Orders API"), x = 0.0, y = 0.0 }, new { elementId = Named("Orders DB"), x = 0.0, y = 260.0 } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var revision = Guid.NewGuid();
        await TestHost.Created(client.PostAsJsonAsync(Url("revisions"), new { id = revision, name = "Review 1" }));

        var pinned = Url($"views/{shop}/export/svg?revisionId={revision}");
        var first = await client.GetByteArrayAsync(pinned);
        XDocument.Parse(Encoding.UTF8.GetString(first));
        Assert.Contains("Orders API", Encoding.UTF8.GetString(first));

        var renamed = await client.PatchAsJsonAsync(Url($"elements/{Named("Orders API")}"), new { name = "Order intake" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        Assert.Equal(first, await client.GetByteArrayAsync(pinned));
        Assert.Contains("Order intake", await client.GetStringAsync(Url($"views/{shop}/export/svg")));

        // The layout is not part of the revision: the file names the layout it was drawn from.
        var before = Regex.Match(Encoding.UTF8.GetString(first), "layout (sha256:[0-9a-f]{64})").Groups[1].Value;
        Assert.NotEmpty(before);
        await client.PutAsJsonAsync(Url($"views/{shop}/layout"), new { positions = new[] { new { elementId = Named("Orders API"), x = 40.0, y = 0.0 } } });
        var moved = await client.GetStringAsync(pinned);
        Assert.DoesNotContain(before, moved);
        Assert.Matches("layout sha256:[0-9a-f]{64}", moved);

        var mermaid = await client.GetAsync(Url($"views/{shop}/export/mermaid?revisionId={revision}"));
        Assert.Equal(HttpStatusCode.OK, mermaid.StatusCode);
        Assert.Contains("subgraph shop", await mermaid.Content.ReadAsStringAsync());

        var dsl = await client.GetStringAsync(Url($"export/structurizr?revisionId={revision}"));
        DiagramExportTests.AssertWellFormedDsl(dsl);
        Assert.Contains("systemLandscape \"landscape\" \"Landscape\"", dsl);
        Assert.Contains("container shop \"shop\" \"Shop\"", dsl);
        Assert.Contains("orders_db = container \"Orders DB\"", dsl);
        // The five relationships of the model, and the two derived lines of the landscape.
        Assert.Equal(7, Regex.Matches(dsl, "^ +[a-z0-9_]+ -> [a-z0-9_]+ ", RegexOptions.Multiline).Count);
        Assert.Equal(2, Regex.Matches(dsl, " \"Derived\"$", RegexOptions.Multiline).Count);

        // A Viewer reads every export; an unknown view or revision is refused.
        var viewer = await TestHost.SignInMemberAsync(fixture.Factory, client, workspaceId, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(pinned)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url($"views/{Guid.NewGuid()}/export/svg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Url($"views/{shop}/export/mermaid?revisionId={Guid.NewGuid()}"))).StatusCode);
    }
}
