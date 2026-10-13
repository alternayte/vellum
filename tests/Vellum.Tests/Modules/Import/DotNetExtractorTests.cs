using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>The .NET rules on small sources: what each recognises, what it refuses, and the exact lines.</summary>
public class DotNetExtractorTests
{
    private const string WebProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <ItemGroup>
            <PackageReference Include="MassTransit.RabbitMQ" Version="8.3.4" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
          </ItemGroup>
        </Project>
        """;

    private static ExtractionResult Api(string source, string project = WebProject) =>
        Repo.Extract(("src/Shop.Api/Shop.Api.csproj", project), ("src/Shop.Api/Code.cs", source));

    private static string[] Operations(ExtractionResult result) =>
        result.Facts.Where(f => f.Kind == FactKind.Operation).Select(f => f.Key).ToArray();

    // ---- project files ----

    [Fact]
    public void A_project_is_a_service_when_it_is_web_worker_or_executable_and_packages_are_dependencies()
    {
        var result = Repo.Extract(
            ("src/Shop.Api/Shop.Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Confluent.Kafka" Version="2.6.0" />
                    <PackageReference Include="Dapper" Version="2.1.35" />
                    <PackageReference Include="Refit.HttpClientFactory" Version="8.0.0" />
                    <PackageReference Include="RabbitMQ.Client">
                      <Version>7.0.0</Version>
                    </PackageReference>
                    <PackageReference Include="Serilog" />
                    <ProjectReference Include="..\Shop.Domain\Shop.Domain.csproj" />
                  </ItemGroup>
                </Project>
                """),
            ("src/Shop.Domain/Shop.Domain.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""),
            ("tools/Seeder/Seeder.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """));

        Assert.Equal(["service:src/Shop.Api", "service:tools/Seeder"], result.Facts.Where(f => f.Kind == FactKind.Service).Select(f => f.Key));
        var service = result.Fact("service:src/Shop.Api");
        Assert.Equal("Shop.Api", service.Name);
        Assert.Equal("web", service.Attr("kind"));
        Assert.Equal("net10.0", service.Attr("targetFramework"));
        Assert.Equal(["src/Shop.Api/Shop.Api.csproj:1-1"], service.Lines());
        Assert.Equal(("dotnet", "src/Shop.Api/Shop.Api.csproj"), (service.Attr("language"), service.Attr("project")));
        Assert.Equal(("dotnet.project", 2), (service.Evidence[0].Rule, service.Evidence[0].RuleVersion));
        Assert.Equal("executable", result.Fact("service:tools/Seeder").Attr("kind"));

        var kafka = result.Fact("dependency:src/Shop.Api:nuget:Confluent.Kafka");
        Assert.Equal(FactKind.Dependency, kafka.Kind);
        Assert.Equal(("kafka", "messaging", "kafka", "2.6.0"), (kafka.Attr("technology"), kafka.Attr("role"), kafka.Attr("broker"), kafka.Attr("version")));
        Assert.Equal(["src/Shop.Api/Shop.Api.csproj:6-6"], kafka.Lines());
        Assert.Equal(("dotnet.package-reference", 2), (kafka.Evidence[0].Rule, kafka.Evidence[0].RuleVersion));
        Assert.Equal("service:src/Shop.Api", kafka.Attr("service"));
        Assert.Equal("dapper", result.Fact("dependency:src/Shop.Api:nuget:Dapper").Attr("technology"));
        Assert.Equal("refit", result.Fact("dependency:src/Shop.Api:nuget:Refit.HttpClientFactory").Attr("technology"));
        Assert.Equal("rabbitmq", result.Fact("dependency:src/Shop.Api:nuget:RabbitMQ.Client").Attr("technology"));
        // An unknown package is a dependency with no claim about what it is.
        var serilog = result.Fact("dependency:src/Shop.Api:nuget:Serilog");
        Assert.Null(serilog.Attr("technology"));
        Assert.Null(serilog.Attr("version"));
        Assert.Equal("../Shop.Domain/Shop.Domain.csproj", result.Fact("dependency:src/Shop.Api:project:Shop.Domain").Attr("reference"));
    }

    [Fact]
    public void A_source_file_with_no_project_above_it_is_noted_and_not_read()
    {
        var result = Repo.Extract(("scripts/Tool.cs", "var app = builder.Build();\napp.MapGet(\"/x\", () => 1);\n"));

        Assert.Empty(result.Facts);
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "no-owning-project", "scripts/Tool.cs", 0)], result.Notes);
    }

    // ---- minimal API ----

    [Fact]
    public void Minimal_api_routes_resolve_static_group_prefixes()
    {
        var result = Api("""
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.MapGet("/", () => "home");
            var api = app.MapGroup("/api").RequireAuthorization();
            var v1 = api.MapGroup("v1");
            v1.MapPut("/items/{sku:alpha}", (string sku) => sku);
            v1.MapGroup("/admin").WithTags("admin").MapDelete("items/{*rest}", (string rest) => rest);
            app.MapGroup("/reports")
                .MapPatch(
                    "/{id:int?}",
                    (int? id) => id);
            app.Run();
            """);

        Assert.Equal(
            [
                "operation:src/Shop.Api:DELETE /api/v1/admin/items/{rest}",
                "operation:src/Shop.Api:GET /",
                "operation:src/Shop.Api:PATCH /reports/{id}",
                "operation:src/Shop.Api:PUT /api/v1/items/{sku}",
            ],
            Operations(result));

        var put = result.Fact("operation:src/Shop.Api:PUT /api/v1/items/{sku}");
        Assert.Equal(("PUT", "/api/v1/items/{sku}", "/api/v1/items/{}", "/api/v1/items/{sku:alpha}"),
            (put.Attr("method"), put.Attr("path"), put.Attr("routeTemplate"), put.Attr("declaredRoute")));
        Assert.Equal("src/Shop.Api/Shop.Api.csproj", put.Attr("project"));
        Assert.Null(put.Attr("conditional"));
        Assert.Equal(("dotnet.minimal-api-route", 2), (put.Evidence[0].Rule, put.Evidence[0].RuleVersion));
        Assert.Equal("aspnetcore-minimal-api", put.Attr("framework"));
        Assert.Equal("service:src/Shop.Api", put.Attr("service"));
        Assert.Equal(["src/Shop.Api/Code.cs:7-7"], put.Lines());
        Assert.Equal(["src/Shop.Api/Code.cs:10-12"], result.Fact("operation:src/Shop.Api:PATCH /reports/{id}").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_route_in_a_comment_or_in_any_kind_of_string_is_not_extracted()
    {
        var result = Api(""""
            var app = WebApplication.CreateBuilder(args).Build();
            // app.MapGet("/line-comment", () => 1);
            /* app.MapGet("/block-comment", () => 1);
               app.MapPost("/block-comment-2", () => 1); */
            /// <code>app.MapGet("/doc-comment", () => 1);</code>
            var a = "app.MapGet(\"/regular-string\", () => 1);";
            var b = @"app.MapGet(""/verbatim-string"", () => 1);
                      app.MapGet(""/verbatim-line-2"", () => 1);";
            var c = $"app.MapGet(\"/interpolated/{a.Length}\", () => {"nested \" quote"});";
            var d = """
                app.MapGet("/raw-string", () => 1);
                """;
            var e = $$"""
                app.MapGet("/raw-interpolated/{{a}}", () => 1);
                """;
            var f = '"'; var g = '\'';
            #if LEGACY
            #error app.MapGet("/preprocessor", () => 1);
            #endif
            app.MapGet("/real", () => 1);
            """");

        Assert.Equal(["operation:src/Shop.Api:GET /real"], Operations(result));
        // The line number after every multi-line literal is still exact.
        Assert.Equal(["src/Shop.Api/Code.cs:20-20"], result.Fact("operation:src/Shop.Api:GET /real").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_route_that_is_not_a_literal_or_a_prefix_that_is_not_known_gives_a_note_and_no_fact()
    {
        var result = Api("""
            public static class Endpoints
            {
                public static void MapShop(this IEndpointRouteBuilder routes, string name)
                {
                    routes.MapGet("/unknown-prefix", () => 1);
                }

                public static void MapAdmin(this WebApplication app, string name, RouteGroupBuilder other)
                {
                    const string Path = "/constant";
                    app.MapGet(Path, () => 1);
                    app.MapGet($"/tenants/{name}", () => 1);
                    app.MapGet("/a" + name, () => 1);
                    app.MapGroup(name).MapGet("/under-dynamic-group", () => 1);
                    other.MapGet("/other", () => 1);
                    app.MapGet("/known", () => 1);
                }
            }
            """);

        Assert.Equal(["operation:src/Shop.Api:GET /known"], Operations(result));
        Assert.Equal(
            [
                (5, "unresolved-route-prefix"), (11, "dynamic-route"), (12, "dynamic-route"), (13, "dynamic-route"),
                (14, "unresolved-route-prefix"), (15, "unresolved-route-prefix"),
            ],
            result.Notes.Select(n => (n.Line, n.Reason)));
        Assert.All(result.Notes, n => Assert.Equal(NoteKind.Undecided, n.Kind));
    }

    [Fact]
    public void A_name_that_is_assigned_two_different_builders_has_no_known_prefix()
    {
        var result = Api("""
            var app = WebApplication.Create(args);
            var group = app.MapGroup("/a");
            group = app.MapGroup("/b");
            group.MapGet("/x", () => 1);
            """);

        Assert.Empty(Operations(result));
        Assert.Equal(["unresolved-route-prefix"], result.Reasons());
    }

    // ---- controllers ----

    [Fact]
    public void Controller_routes_join_the_class_route_and_replace_the_tokens()
    {
        var result = Api("""
            namespace Shop.Api.Controllers;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class OrdersController(IOrders orders) : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();

                [HttpGet("{id:guid}", Name = "GetOrder")]
                [ProducesResponseType(200)]
                public async Task<IActionResult> Get(
                    Guid id,
                    CancellationToken ct)
                {
                    var hint = "[HttpDelete(\"in-a-string\")]";
                    return Ok();
                }

                [HttpPost("[action]"), Authorize]
                public IActionResult Cancel() => Ok();

                [HttpPut("/absolute/{id}")]
                public IActionResult Replace(int id) => Ok();

                [HttpDelete, Route("purge")]
                public IActionResult Purge() => Ok();

                // [HttpPatch("commented")]
                [Obsolete]
                public string NotAnEndpoint() => "";

                [HttpGet(RouteNames.Search)]
                public IActionResult Search() => Ok();
            }

            public class PlainController : Controller
            {
                [HttpGet("ping")]
                public string Ping() => "pong";

                [HttpGet]
                public string Index() => "";
            }

            public class NotAController
            {
                [HttpGet("never")]
                public string Never() => "";
            }
            """);

        Assert.Equal(
            [
                "operation:src/Shop.Api:DELETE /api/Orders/purge",
                "operation:src/Shop.Api:GET /api/Orders",
                "operation:src/Shop.Api:GET /api/Orders/{id}",
                "operation:src/Shop.Api:GET /ping",
                "operation:src/Shop.Api:POST /api/Orders/Cancel",
                "operation:src/Shop.Api:PUT /absolute/{id}",
            ],
            Operations(result));

        var get = result.Fact("operation:src/Shop.Api:GET /api/Orders/{id}");
        Assert.Equal("aspnetcore-controller", get.Attr("framework"));
        Assert.Equal("OrdersController", get.Attr("controller"));
        Assert.Equal("Get", get.Attr("action"));
        Assert.Equal(("/api/Orders/{id}", "/api/Orders/{}", "api/[controller]/{id:guid}"),
            (get.Attr("path"), get.Attr("routeTemplate"), get.Attr("declaredRoute")));
        Assert.Equal(("dotnet.controller-route", 2), (get.Evidence[0].Rule, get.Evidence[0].RuleVersion));
        // From the first attribute to the end of the parameter list.
        Assert.Equal(["src/Shop.Api/Code.cs:10-14"], get.Lines());
        Assert.Equal(["src/Shop.Api/Code.cs:7-8"], result.Fact("operation:src/Shop.Api:GET /api/Orders").Lines());
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "dynamic-route", "src/Shop.Api/Code.cs", 33),
                // No template on the member or the class: the path comes from conventional routing.
                new ExtractionNote(NoteKind.Undecided, "conventional-route", "src/Shop.Api/Code.cs", 42),
            ],
            result.Notes);
    }

    // ---- MassTransit ----

    [Fact]
    public void Consumers_publishes_and_sends_are_found_and_a_message_in_a_variable_is_noted()
    {
        var result = Api("""
            using MassTransit;

            namespace Shop.Api;

            public record OrderPlaced(Guid OrderId, string Sku = "none");
            public interface IStockLow { string Sku { get; } int Remaining { get; } }
            public class ShipOrder
            {
                public Guid OrderId { get; init; }
                public required List<string> Skus { get; set; }
                internal string Hidden { get; set; } = "";
                public static string Version { get; } = "1";
            }

            public class OrderPlacedConsumer : IConsumer<OrderPlaced>, IConsumer<Shop.Api.IStockLow>
            {
                private readonly ISendEndpointProvider _sender;
                private readonly IMediator _mediator;

                public async Task Consume(ConsumeContext<OrderPlaced> context)
                {
                    await context.Publish<IStockLow>(new { Sku = "a", Remaining = 1 });
                    var endpoint = await _sender.GetSendEndpoint(new Uri("queue:shipping"));
                    await _sender.Send(new ShipOrder { OrderId = context.Message.OrderId, Skus = [] });
                    await _mediator.Send(new NotABusMessage());
                    var later = new OrderPlaced(Guid.NewGuid());
                    await context.Publish(later);
                    // await context.Publish(new Commented());
                }

                public Task Consume(ConsumeContext<IStockLow> context) => Task.CompletedTask;
            }
            """);

        Assert.Equal(
            [
                "subscription:src/Shop.Api:Shop.Api.OrderPlacedConsumer:Shop.Api.IStockLow",
                "subscription:src/Shop.Api:Shop.Api.OrderPlacedConsumer:Shop.Api.OrderPlaced",
            ],
            result.Facts.Where(f => f.Kind == FactKind.Subscription).Select(f => f.Key));
        var consumer = result.Fact("subscription:src/Shop.Api:Shop.Api.OrderPlacedConsumer:Shop.Api.OrderPlaced");
        Assert.Equal("OrderPlacedConsumer consumes OrderPlaced", consumer.Name);
        Assert.Equal(("message:Shop.Api.OrderPlaced", "OrderPlaced", "OrderPlacedConsumer", "masstransit"),
            (consumer.Attr("message"), consumer.Attr("messageType"), consumer.Attr("consumer"), consumer.Attr("transport")));
        // The type is written with its namespace at the other use site, and resolves to the same kind of key.
        var stock = result.Fact("subscription:src/Shop.Api:Shop.Api.OrderPlacedConsumer:Shop.Api.IStockLow");
        Assert.Equal(("message:Shop.Api.IStockLow", "Shop.Api.IStockLow"), (stock.Attr("message"), stock.Attr("messageType")));
        Assert.Null(consumer.Attr("queue"));
        Assert.Equal(["src/Shop.Api/Code.cs:15-16"], consumer.Lines());

        Assert.Equal(
            ["publication:src/Shop.Api:Shop.Api.IStockLow", "publication:src/Shop.Api:Shop.Api.ShipOrder"],
            result.Facts.Where(f => f.Kind == FactKind.Publication).Select(f => f.Key));
        var published = result.Fact("publication:src/Shop.Api:Shop.Api.IStockLow");
        Assert.Equal(("publish", "message:Shop.Api.IStockLow", "IStockLow"), (published.Attr("mode"), published.Attr("message"), published.Attr("messageType")));
        Assert.Equal(["src/Shop.Api/Code.cs:22-22"], published.Lines());
        Assert.Equal(("dotnet.masstransit-publish", 2), (published.Evidence[0].Rule, published.Evidence[0].RuleVersion));
        Assert.Equal("send", result.Fact("publication:src/Shop.Api:Shop.Api.ShipOrder").Attr("mode"));
        Assert.Equal(["src/Shop.Api/Code.cs:24-24"], result.Fact("publication:src/Shop.Api:Shop.Api.ShipOrder").Lines());

        // The contracts of the three message types that a consumer or a publish names.
        Assert.Equal(
            ["message:Shop.Api.IStockLow", "message:Shop.Api.OrderPlaced", "message:Shop.Api.ShipOrder"],
            result.Facts.Where(f => f.Kind == FactKind.Message).Select(f => f.Key));
        var placed = result.Fact("message:Shop.Api.OrderPlaced");
        Assert.Equal("OrderPlaced", placed.Name);
        Assert.Equal("src/Shop.Api/Shop.Api.csproj", placed.Attr("declaredIn"));
        Assert.Equal(("dotnet.message-contract", 2), (placed.Evidence[0].Rule, placed.Evidence[0].RuleVersion));
        Assert.Equal("Shop.Api.OrderPlaced", placed.Attr("clrType"));
        Assert.Equal("OrderId:Guid;Sku:string", placed.Attr("fields"));
        Assert.Equal(["src/Shop.Api/Code.cs:5-5"], placed.Lines());
        Assert.Equal("Remaining:int;Sku:string", result.Fact("message:Shop.Api.IStockLow").Attr("fields"));
        Assert.Equal("OrderId:Guid;Skus:List<string>", result.Fact("message:Shop.Api.ShipOrder").Attr("fields"));
        Assert.Equal(["src/Shop.Api/Code.cs:7-13"], result.Fact("message:Shop.Api.ShipOrder").Lines());
        Assert.DoesNotContain(result.Facts, f => f.Key.Contains("NotABusMessage", StringComparison.Ordinal));

        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-message-type", "src/Shop.Api/Code.cs", 27)], result.Notes);
    }

    [Fact]
    public void Publish_and_send_are_ignored_in_a_project_that_does_not_use_MassTransit()
    {
        var result = Api("""
            public class Handler(IPublishEndpoint bus)
            {
                public Task Run() => bus.Publish(new Thing());
            }
            public class ThingConsumer : IConsumer<Thing> { }
            """, """<Project Sdk="Microsoft.NET.Sdk.Web" />""");

        Assert.Equal(["service:src/Shop.Api"], result.Facts.Select(f => f.Key));
    }

    [Fact]
    public void A_dynamic_receive_endpoint_name_is_noted_and_the_consumer_has_no_queue()
    {
        var result = Api("""
            using MassTransit;
            public class C : IConsumer<Thing> { }
            public static class Bus
            {
                public static void Configure(IRabbitMqBusFactoryConfigurator cfg, IBusRegistrationContext context, string name)
                {
                    cfg.ReceiveEndpoint(name + "-things", e => e.ConfigureConsumer<C>(context));
                }
            }
            """);

        // "Thing" is declared nowhere in the scan: the subscription says what is written and points at no contract.
        var subscription = result.Fact("subscription:src/Shop.Api:C:unresolved:Thing");
        Assert.Equal((null, "Thing", null), (subscription.Attr("message"), subscription.Attr("messageType"), subscription.Attr("queue")));
        Assert.DoesNotContain(result.Facts, f => f.Kind is FactKind.Channel or FactKind.Message);
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "unresolved-message-type", "src/Shop.Api/Code.cs", 2),
                new ExtractionNote(NoteKind.Undecided, "dynamic-queue-name", "src/Shop.Api/Code.cs", 7),
            ],
            result.Notes);
    }

    // ---- outbox ----

    [Fact]
    public void The_outbox_tables_alone_are_not_an_outbox()
    {
        var result = Api("""
            using MassTransit;
            public class Db : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.AddOutboxMessageEntity();
                    // bus.AddEntityFrameworkOutbox<Db>(o => o.UseBusOutbox());
                }
            }
            """);

        Assert.DoesNotContain(result.Facts, f => f.Key.StartsWith("outbox:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_in_memory_outbox_is_a_different_fact_from_the_transactional_one()
    {
        var result = Api("""
            using MassTransit;
            public static class Bus
            {
                public static void Configure(IBusRegistrationConfigurator bus)
                {
                    bus.AddEntityFrameworkOutbox<ShopDb>(o => o.UseSqlServer());
                    bus.AddInMemoryOutbox();
                }
            }
            """);

        var transactional = result.Fact("outbox:src/Shop.Api:masstransit-entity-framework");
        Assert.Equal((FactKind.Pattern, "transactional-outbox", "masstransit-entity-framework"),
            (transactional.Kind, transactional.Attr("pattern"), transactional.Attr("mechanism")));
        Assert.Equal(("dotnet.masstransit-outbox", 2), (transactional.Evidence[0].Rule, transactional.Evidence[0].RuleVersion));
        Assert.Equal("ShopDb", transactional.Attr("dbContext"));
        Assert.Equal("false", transactional.Attr("busOutbox"));
        Assert.Equal("false", transactional.Attr("outboxEntities"));
        Assert.Equal(["src/Shop.Api/Code.cs:6-6"], transactional.Lines());
        var inMemory = result.Fact("outbox:src/Shop.Api:masstransit-in-memory");
        Assert.Equal((FactKind.Pattern, "in-memory-outbox"), (inMemory.Kind, inMemory.Attr("pattern")));
        Assert.DoesNotContain(result.Facts, f => f.Kind == FactKind.Publication);
        Assert.Equal(["src/Shop.Api/Code.cs:7-7"], inMemory.Lines());
    }

    [Fact]
    public void A_hand_written_outbox_needs_the_table_and_a_dispatcher_that_reads_it()
    {
        const string Context = """
            public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
            {
                public DbSet<Product> Products { get; set; }
                public DbSet<OutboxMessage> Outbox { get; set; }
            }
            """;
        const string Dispatcher = """
            public sealed class OutboxDispatcher(IServiceScopeFactory scopes) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
                    var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
                    var pending = await db.Outbox.Where(m => m.SentAt == null).ToListAsync(stoppingToken);
                    foreach (var message in pending)
                        await bus.Publish(message.ToEvent(), stoppingToken);
                }
            }
            """;
        // Reads the same table and publishes nothing: a retention job, not a dispatcher.
        const string Retention = """
            public sealed class OutboxRetention(IServiceScopeFactory scopes) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
                    await db.Outbox.Where(m => m.SentAt != null).ExecuteDeleteAsync(stoppingToken);
                }
            }
            """;
        // Publishes, and writes to the table without reading it: a producer, not a dispatcher.
        // The publish through the mediator is in-process and is not a publish API.
        const string Writer = """
            public sealed class Importer(ShopDb db, IBus bus, IMediator mediator) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    db.Outbox.Add(new OutboxMessage());
                    await bus.Publish(new Imported(), stoppingToken);
                    await mediator.Publish(new Imported(), stoppingToken);
                }
            }
            """;
        // Reads the table, and "publishes" only through the mediator.
        const string InProcess = """
            public sealed class Notifier(ShopDb db, IMediator mediator) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    foreach (var message in await db.Outbox.ToListAsync(stoppingToken))
                        await mediator.Publish(message, stoppingToken);
                }
            }
            """;
        const string Unrelated = """
            public sealed class CacheWarmer : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
            }
            """;
        const string PlainProject = """<Project Sdk="Microsoft.NET.Sdk.Worker" />""";

        var both = Repo.Extract(
            ("Shop/Shop.csproj", PlainProject), ("Shop/ShopDb.cs", Context), ("Shop/OutboxDispatcher.cs", Dispatcher), ("Shop/CacheWarmer.cs", Unrelated));
        var outbox = both.Fact("outbox:Shop:hand-written");
        Assert.Equal(FactKind.Pattern, outbox.Kind);
        Assert.Equal("service:Shop", outbox.Attr("service"));
        Assert.Equal(
            ("transactional-outbox", "hand-written", "ShopDb", "OutboxMessage", "OutboxDispatcher"),
            (outbox.Attr("pattern"), outbox.Attr("mechanism"), outbox.Attr("dbContext"), outbox.Attr("entity"), outbox.Attr("dispatcher")));
        // The class, the read of the set, the publish call, and the set.
        Assert.Equal(
            ["Shop/OutboxDispatcher.cs:1-2", "Shop/OutboxDispatcher.cs:8-8", "Shop/OutboxDispatcher.cs:10-10", "Shop/ShopDb.cs:4-4"],
            outbox.Lines());
        Assert.All(outbox.Evidence, e => Assert.Equal(("dotnet.outbox-handwritten", 2), (e.Rule, e.RuleVersion)));
        Assert.DoesNotContain(both.Facts, f => f.Kind == FactKind.Publication);

        var tableOnly = Repo.Extract(("Shop/Shop.csproj", PlainProject), ("Shop/ShopDb.cs", Context), ("Shop/CacheWarmer.cs", Unrelated));
        Assert.DoesNotContain(tableOnly.Facts, f => f.Kind == FactKind.Pattern);

        var dispatcherOnly = Repo.Extract(("Shop/Shop.csproj", PlainProject), ("Shop/OutboxDispatcher.cs", Dispatcher));
        Assert.DoesNotContain(dispatcherOnly.Facts, f => f.Kind == FactKind.Pattern);

        foreach (var (name, lookAlike) in new[] { ("OutboxRetention", Retention), ("Importer", Writer), ("Notifier", InProcess) })
        {
            var result = Repo.Extract(("Shop/Shop.csproj", PlainProject), ("Shop/ShopDb.cs", Context), ($"Shop/{name}.cs", lookAlike));
            Assert.DoesNotContain(result.Facts, f => f.Kind is FactKind.Pattern or FactKind.Publication);
        }
    }

    // ---- EF Core ----

    [Fact]
    public void A_DbContext_gives_data_access_with_its_sets_and_the_store_of_the_one_provider_package()
    {
        var result = Api("""
            public class ShopDb : IdentityDbContext<User>
            {
                public DbSet<Product> Products { get; set; } = null!;
                public DbSet<Dictionary<string, Tag>> Tags => Set<Dictionary<string, Tag>>();
                public string NotASet { get; set; }
            }
            public class NotAContext : SomethingElse { public DbSet<X> Xs { get; set; } }
            """);

        var access = result.Fact("dataaccess:src/Shop.Api:ShopDb");
        Assert.Equal(FactKind.DataAccess, access.Kind);
        Assert.Equal("ShopDb", access.Name);
        Assert.Equal(("efcore", "ShopDb", "service:src/Shop.Api"), (access.Attr("technology"), access.Attr("dbContext"), access.Attr("service")));
        Assert.Equal("Dictionary<string,Tag>,Product", access.Attr("entities"));
        Assert.All(access.Evidence, e => Assert.Equal(("dotnet.efcore-dbcontext", 2), (e.Rule, e.RuleVersion)));
        Assert.Equal("sqlserver", access.Attr("storeKind"));
        Assert.Equal(["src/Shop.Api/Code.cs:1-2", "src/Shop.Api/Code.cs:3-3", "src/Shop.Api/Code.cs:4-4"], access.Lines());
        Assert.False(result.Has("dataaccess:src/Shop.Api:NotAContext"));
    }

    [Fact]
    public void The_store_is_unknown_when_no_provider_or_two_providers_are_referenced()
    {
        // The key holds the namespace of the context, here from a block namespace.
        const string Source = "namespace Shop.Data { public class Db : DbContext { public DbSet<A> As { get; set; } } }";

        Assert.Equal("unknown", Api(Source, """<Project Sdk="Microsoft.NET.Sdk.Web" />""").Fact("dataaccess:src/Shop.Api:Shop.Data.Db").Attr("storeKind"));
        Assert.Equal("unknown", Api(Source, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.2" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """).Fact("dataaccess:src/Shop.Api:Shop.Data.Db").Attr("storeKind"));
    }

    // ---- project identity (review item 16) ----

    [Fact]
    public void Two_projects_with_one_name_in_different_folders_have_different_keys()
    {
        const string Project = """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """;
        const string Program = "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/ping\", () => 1);\n";

        var result = Repo.Extract(
            ("a/Api/Api.csproj", Project), ("a/Api/Program.cs", Program),
            ("b/Api/Api.csproj", Project), ("b/Api/Program.cs", Program));

        Assert.Equal(["service:a/Api", "service:b/Api"], result.Facts.Where(f => f.Kind == FactKind.Service).Select(f => f.Key));
        Assert.All(result.Facts.Where(f => f.Kind == FactKind.Service), f => Assert.Equal("Api", f.Name));
        Assert.Equal(["operation:a/Api:GET /ping", "operation:b/Api:GET /ping"], Operations(result));
        Assert.Equal("service:b/Api", result.Fact("operation:b/Api:GET /ping").Attr("service"));
        Assert.Equal(["b/Api/Program.cs:2-2"], result.Fact("operation:b/Api:GET /ping").Lines());
        Assert.Equal(
            ["dependency:a/Api:nuget:Serilog", "dependency:b/Api:nuget:Serilog"],
            result.Facts.Where(f => f.Kind == FactKind.Dependency).Select(f => f.Key));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Two_project_files_in_one_directory_are_two_services_and_own_no_source_file()
    {
        var result = Repo.Extract(
            ("svc/A.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web" />"""),
            ("svc/B.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"><ItemGroup><PackageReference Include="Serilog" Version="4.0.0" /></ItemGroup></Project>"""),
            ("svc/Program.cs", "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/ping\", () => 1);\n"));

        // Nothing in the text says which of the two projects compiles Program.cs.
        Assert.Equal(
            ["dependency:svc#svc/B.csproj:nuget:Serilog", "service:svc#svc/A.csproj", "service:svc#svc/B.csproj"],
            result.Facts.Select(f => f.Key));
        Assert.Equal(("A", "B"), (result.Fact("service:svc#svc/A.csproj").Name, result.Fact("service:svc#svc/B.csproj").Name));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "ambiguous-owning-project", "svc/Program.cs", 0)], result.Notes);
    }

    // ---- minimal API scopes (review item 6) ----

    [Fact]
    public void A_builder_name_is_resolved_in_the_scope_of_the_call_and_a_name_that_is_not_a_builder_gives_nothing()
    {
        var result = Api("""
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            var orders = app.MapGroup("/orders");
            orders.MapOrders();

            var cache = new RouteCache();
            cache.MapGet("/not-a-route", 5);
            MyRouter router = new();
            router.MapPost("/also-not", 1);

            void Local(RouteGroupBuilder app)
            {
                app.MapGet("/shadowed", () => 1);
            }

            Configure(app => app.MapGet("/lambda-parameter", () => 1));
            app.MapGet("/top", () => 1);

            public static class OrderEndpoints
            {
                public static void MapOrders(this RouteGroupBuilder app)
                {
                    app.MapGet("/{id}", (int id) => id);
                }

                public static void Other()
                {
                    app.MapDelete("/outer-name-is-not-visible", () => 1);
                }
            }
            """);

        Assert.Equal(["operation:src/Shop.Api:GET /top"], Operations(result));
        Assert.Equal(["src/Shop.Api/Code.cs:17-17"], result.Fact("operation:src/Shop.Api:GET /top").Lines());
        // Lines 7 and 9 are calls on objects that are not endpoint builders: no fact and no note.
        // The others are builders, or may be, with a prefix that this text does not give.
        Assert.Equal(
            [(13, "unresolved-route-prefix"), (16, "unresolved-route-prefix"), (23, "unresolved-route-prefix"), (28, "unresolved-route-prefix")],
            result.Notes.Select(n => (n.Line, n.Reason)));
    }

    [Fact]
    public void Every_form_of_inner_declaration_hides_an_outer_builder_of_the_same_name()
    {
        var result = Api("""
            var app = WebApplication.Create(args);
            void A() { if (TryGet(out RouteGroupBuilder app)) app.MapGet("/out", () => 1); }
            void B(object o) { if (o is IEndpointRouteBuilder app && app is not null) app.MapGet("/pattern", () => 1); }
            void C() { var (app, other) = Pair(); app.MapGet("/deconstructed", () => 1); }
            app.MapGet("/top", () => 1);
            abstract class K { public abstract void M(WebApplication app); public void N() { app.MapGet("/k", () => 1); } }
            class P { public WebApplication App { get; } public void N() { App.MapGet("/property", () => 1); } }
            """);

        // A property of the class with the type WebApplication is the application; the parameter of
        // the abstract method of K is not in scope in N.
        Assert.Equal(["operation:src/Shop.Api:GET /property", "operation:src/Shop.Api:GET /top"], Operations(result));
        Assert.Equal(
            [(2, "unresolved-route-prefix"), (3, "unresolved-route-prefix"), (4, "unresolved-route-prefix"), (6, "unresolved-route-prefix")],
            result.Notes.Select(n => (n.Line, n.Reason)));
    }

    [Fact]
    public void Build_gives_the_application_only_on_a_web_application_builder()
    {
        var result = Api("""
            var builder = Host.CreateApplicationBuilder(args);
            var app = builder.Build();
            app.MapGet("/x", () => 1);
            var other = factory.Build();
            other.MapGet("/y", () => 1);
            """);

        Assert.Empty(Operations(result));
        Assert.Equal([(3, "unresolved-route-prefix"), (5, "unresolved-route-prefix")], result.Notes.Select(n => (n.Line, n.Reason)));
    }

    // ---- controllers across files (review item 7) ----

    private const string CrudBase = """
        namespace Shop.Api;

        [ApiController]
        [Route("api/[controller]")]
        public abstract class CrudController : ControllerBase
        {
            [HttpGet("{id}")]
            public IActionResult Get(int id) => Ok();

            [HttpDelete("{id}")]
            public virtual IActionResult Delete(int id) => Ok();
        }
        """;

    private const string Products = """
        namespace Shop.Api;

        public class ProductsController : CrudController
        {
            [HttpPost]
            public IActionResult Create() => Ok();

            public override IActionResult Delete(int id) => NotFound();
        }
        """;

    [Fact]
    public void A_controller_takes_the_route_and_the_actions_of_a_base_class_that_is_in_the_scan()
    {
        var result = Repo.Extract(
            ("src/Shop.Api/Shop.Api.csproj", WebProject), ("src/Shop.Api/CrudController.cs", CrudBase), ("src/Shop.Api/Products.cs", Products));

        // The abstract class has no routes of its own ("/api/Crud/…" does not exist).
        Assert.Equal(["operation:src/Shop.Api:GET /api/Products/{id}", "operation:src/Shop.Api:POST /api/Products"], Operations(result));
        var create = result.Fact("operation:src/Shop.Api:POST /api/Products");
        Assert.Equal(("ProductsController", "Create", "api/[controller]"), (create.Attr("controller"), create.Attr("action"), create.Attr("declaredRoute")));
        Assert.Equal(["src/Shop.Api/Products.cs:5-6"], create.Lines());
        // The inherited action: where it is declared, and the class that inherits it.
        var get = result.Fact("operation:src/Shop.Api:GET /api/Products/{id}");
        Assert.Equal(("ProductsController", "Get"), (get.Attr("controller"), get.Attr("action")));
        Assert.Equal(["src/Shop.Api/CrudController.cs:7-8", "src/Shop.Api/Products.cs:3-4"], get.Lines());
        // Delete is declared again in the derived class; which attributes apply is not read.
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "overridden-action", "src/Shop.Api/Products.cs", 3)], result.Notes);
    }

    [Fact]
    public void A_controller_whose_base_class_is_not_in_the_scan_gives_a_note_and_no_routes()
    {
        var result = Repo.Extract(("src/Shop.Api/Shop.Api.csproj", WebProject), ("src/Shop.Api/Products.cs", Products));

        Assert.Empty(Operations(result));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "unresolved-base-class", "src/Shop.Api/Products.cs", 3)], result.Notes);
    }

    [Fact]
    public void A_partial_controller_is_read_from_all_its_parts_and_noted_when_only_one_part_is_in_the_scan()
    {
        const string Main = """
            namespace Shop.Api;
            [ApiController]
            [Route("api/orders")]
            public partial class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """;
        const string Actions = """
            namespace Shop.Api;
            public partial class OrdersController
            {
                [HttpPost("{id}/cancel")]
                public IActionResult Cancel(int id) => Ok();
            }
            """;

        var both = Repo.Extract(
            ("src/Shop.Api/Shop.Api.csproj", WebProject), ("src/Shop.Api/Orders.cs", Main), ("src/Shop.Api/Orders.Actions.cs", Actions));
        Assert.Equal(["operation:src/Shop.Api:GET /api/orders", "operation:src/Shop.Api:POST /api/orders/{id}/cancel"], Operations(both));
        Assert.Equal(["src/Shop.Api/Orders.Actions.cs:4-5"], both.Fact("operation:src/Shop.Api:POST /api/orders/{id}/cancel").Lines());
        Assert.Empty(both.Notes);

        // The class route can stand on a part that is not in the scan.
        var actionsOnly = Repo.Extract(("src/Shop.Api/Shop.Api.csproj", WebProject), ("src/Shop.Api/Orders.Actions.cs", Actions));
        Assert.Empty(Operations(actionsOnly));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "partial-part-missing", "src/Shop.Api/Orders.Actions.cs", 2)], actionsOnly.Notes);

        var mainOnly = Repo.Extract(("src/Shop.Api/Shop.Api.csproj", WebProject), ("src/Shop.Api/Orders.cs", Main));
        Assert.Empty(Operations(mainOnly));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "partial-part-missing", "src/Shop.Api/Orders.cs", 4)], mainOnly.Notes);
    }

    [Fact]
    public void A_verb_attribute_gives_a_route_only_on_a_public_instance_method()
    {
        var result = Api("""
            [ApiController]
            [Route("api/things")]
            public class ThingsController : ControllerBase
            {
                [HttpGet("delegate")]
                public delegate void Handler(int x);

                [HttpGet("property")]
                public string Name { get; set; }

                [HttpGet("field")]
                public Func<int> Field = Make();

                [HttpGet("private")]
                private IActionResult Hidden() => Ok();

                [HttpGet("static")]
                public static IActionResult Shared() => null;

                [HttpGet("real")]
                public IActionResult Real() => Ok();
            }

            [ApiController]
            [Route("api/abstract")]
            public abstract class AbstractController : ControllerBase
            {
                [HttpGet("never")]
                public IActionResult Never() => Ok();
            }
            """);

        Assert.Equal(["operation:src/Shop.Api:GET /api/things/real"], Operations(result));
        Assert.Equal(["src/Shop.Api/Code.cs:20-21"], result.Fact("operation:src/Shop.Api:GET /api/things/real").Lines());
        Assert.Empty(result.Notes);
    }

    // ---- preprocessor (review item 8) ----

    [Fact]
    public void Inactive_preprocessor_branches_give_nothing_and_undecided_ones_give_conditional_facts()
    {
        var result = Api("""
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            #if false
            app.MapGet("/disabled", () => 1);
            #else
            app.MapGet("/else-of-false", () => 1);
            #endif
            #if true
            app.MapGet("/enabled", () => 1);
            #else
            app.MapGet("/else-of-true", () => "not tokenized: " ( { [);
            #endif
            #if DEBUG
            app.MapGet("/debug", () => 1);
            #elif TRACE&&!MOBILE // a comment
            app.MapGet("/trace", () => 1);
            #else
            app.MapGet("/release", () => 1);
            #endif
              #  if FALSE
            app.MapGet("/upper-false", () => 1);
            #else
            app.MapGet("/not-upper-false", () => 1);
            #endif
            #if DEBUG
            #if false
            app.MapGet("/nested-disabled", () => 1);
            #elif NET10_0 || NET9_0
            app.MapGet("/nested", () => 1);
            #endif
            #endif
            #if !true || (false && X)
            app.MapGet("/computed-false", () => 1);
            #endif
            app.MapGet("/always", () => 1);
            """);

        (string Key, string? Conditional)[] expected =
        [
            ("operation:src/Shop.Api:GET /always", null),
            ("operation:src/Shop.Api:GET /debug", "DEBUG"),
            ("operation:src/Shop.Api:GET /else-of-false", null),
            ("operation:src/Shop.Api:GET /enabled", null),
            ("operation:src/Shop.Api:GET /nested", "DEBUG && (NET10_0 || NET9_0)"),
            // "FALSE" is a symbol. Its own branch is treated as inactive, and the other branch is not treated as certain.
            ("operation:src/Shop.Api:GET /not-upper-false", "!FALSE"),
            ("operation:src/Shop.Api:GET /release", "!DEBUG && !(TRACE && !MOBILE)"),
            ("operation:src/Shop.Api:GET /trace", "!DEBUG && TRACE && !MOBILE"),
        ];
        Assert.Equal(expected, result.Facts.Where(f => f.Kind == FactKind.Operation).Select(f => (f.Key, f.Attr("conditional"))));
        // Line numbers stay exact after the skipped text.
        Assert.Equal(["src/Shop.Api/Code.cs:35-35"], result.Fact("operation:src/Shop.Api:GET /always").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_type_in_a_conditional_branch_gives_conditional_facts_and_a_condition_that_cannot_be_read_gives_none()
    {
        var result = Api("""
            using MassTransit;
            namespace Shop.Api;
            public record Ping(int N);
            #if LEGACY_BUS
            public class PingConsumer : IConsumer<Ping> { }
            #endif
            #if SOME_VERSION >= 3
            public class NewPingConsumer : IConsumer<Ping> { }
            #endif
            """);

        var subscription = Assert.Single(result.Facts, f => f.Kind == FactKind.Subscription);
        Assert.Equal(("subscription:src/Shop.Api:Shop.Api.PingConsumer:Shop.Api.Ping", "LEGACY_BUS"), (subscription.Key, subscription.Attr("conditional")));
        Assert.Null(result.Fact("message:Shop.Api.Ping").Attr("conditional"));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "preprocessor-condition-unreadable", "src/Shop.Api/Code.cs", 7)], result.Notes);
    }

    // ---- MassTransit receivers and open generics (review item 9) ----

    [Fact]
    public void An_open_generic_message_and_a_receiver_that_is_not_a_bus_in_its_own_scope_give_nothing()
    {
        var result = Api("""
            using MassTransit;
            namespace Shop.Api;
            public record OrderPlaced(Guid OrderId);
            public class GenericConsumer<T> : IConsumer<T> where T : class
            {
                public Task Consume(ConsumeContext<T> context) => context.Publish<T>(context.Message);
            }
            public class Relay(IPublishEndpoint bus)
            {
                public Task Forward<TEvent>(TEvent message) where TEvent : class => bus.Publish<TEvent>(message);
                public Task Placed() => bus.Publish(new OrderPlaced(Guid.NewGuid()));
            }
            public class Handler(IMediator bus, ISender sender)
            {
                public Task Run() => bus.Send(new OrderPlaced(Guid.Empty));
                public Task Other() => bus.Publish(new OrderPlaced(Guid.Empty));
            }
            public class Unscoped
            {
                public Task Run() => bus.Publish(new OrderPlaced(Guid.Empty));
                public Task Configure(IRabbitMqBusFactoryConfigurator cfg) => cfg.Publish<OrderPlaced>(p => p.Durable = true);
            }
            """);

        Assert.DoesNotContain(result.Facts, f => f.Kind == FactKind.Subscription);
        var publication = Assert.Single(result.Facts, f => f.Kind == FactKind.Publication);
        Assert.Equal("publication:src/Shop.Api:Shop.Api.OrderPlaced", publication.Key);
        Assert.Equal(("publish", "message:Shop.Api.OrderPlaced", "OrderPlaced"), (publication.Attr("mode"), publication.Attr("message"), publication.Attr("messageType")));
        // Only the publish in Relay, where "bus" is an IPublishEndpoint.
        Assert.Equal(["src/Shop.Api/Code.cs:11-11"], publication.Lines());
        Assert.Empty(result.Notes);
    }

    // ---- message identity (review items 4 and 16) ----

    [Fact]
    public void Message_types_with_one_name_in_two_namespaces_are_two_contracts_and_an_ambiguous_use_points_at_neither()
    {
        var result = Repo.Extract(
            ("contracts/Contracts.csproj", """<Project Sdk="Microsoft.NET.Sdk" />"""),
            ("contracts/Orders.cs", "namespace Orders.Contracts;\npublic record OrderCreated(Guid OrderId);\n"),
            ("contracts/Billing.cs", "namespace Billing.Contracts;\npublic record OrderCreated(Guid InvoiceId, string Token);\n"),
            ("worker/Worker.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"><ItemGroup><PackageReference Include="MassTransit.RabbitMQ" Version="8.3.4" /></ItemGroup></Project>"""),
            ("worker/Consumers.cs", """
                using MassTransit;
                using Orders.Contracts;
                namespace Worker;
                public class OrdersConsumer : IConsumer<OrderCreated>
                {
                    public Task Consume(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
                }
                public class BillingConsumer : IConsumer<Billing.Contracts.OrderCreated>
                {
                    public Task Consume(ConsumeContext<Billing.Contracts.OrderCreated> context) => Task.CompletedTask;
                }
                """),
            ("worker/Ambiguous.cs", """
                using MassTransit;
                using Orders.Contracts;
                using Billing.Contracts;
                namespace Worker;
                public class AmbiguousConsumer : IConsumer<OrderCreated> { }
                public class MissingConsumer : IConsumer<Elsewhere.Shipped> { }
                """));

        Assert.Equal(
            ["message:Billing.Contracts.OrderCreated", "message:Orders.Contracts.OrderCreated"],
            result.Facts.Where(f => f.Kind == FactKind.Message).Select(f => f.Key));
        var billing = result.Fact("message:Billing.Contracts.OrderCreated");
        Assert.Equal(("OrderCreated", "Billing.Contracts.OrderCreated", "contracts/Contracts.csproj", "InvoiceId:Guid;Token:string"),
            (billing.Name, billing.Attr("clrType"), billing.Attr("declaredIn"), billing.Attr("fields")));
        Assert.Equal(["contracts/Billing.cs:2-2"], billing.Lines());
        Assert.Equal("OrderId:Guid", result.Fact("message:Orders.Contracts.OrderCreated").Attr("fields"));

        (string Key, string? Message, string? Written)[] expected =
        [
            ("subscription:worker:Worker.AmbiguousConsumer:unresolved:OrderCreated", null, "OrderCreated"),
            ("subscription:worker:Worker.BillingConsumer:Billing.Contracts.OrderCreated", "message:Billing.Contracts.OrderCreated", "Billing.Contracts.OrderCreated"),
            ("subscription:worker:Worker.MissingConsumer:unresolved:Elsewhere.Shipped", null, "Elsewhere.Shipped"),
            ("subscription:worker:Worker.OrdersConsumer:Orders.Contracts.OrderCreated", "message:Orders.Contracts.OrderCreated", "OrderCreated"),
        ];
        Assert.Equal(expected, result.Facts.Where(f => f.Kind == FactKind.Subscription).Select(f => (f.Key, f.Attr("message"), f.Attr("messageType"))));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "unresolved-message-type", "worker/Ambiguous.cs", 5),
                new ExtractionNote(NoteKind.Undecided, "unresolved-message-type", "worker/Ambiguous.cs", 6),
            ],
            result.Notes);
    }

    [Fact]
    public void A_global_using_of_another_file_of_the_project_makes_a_namespace_visible()
    {
        var result = Repo.Extract(
            ("worker/Worker.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"><ItemGroup><PackageReference Include="MassTransit" Version="8.3.4" /></ItemGroup></Project>"""),
            ("worker/Usings.cs", "global using MassTransit;\nglobal using Orders.Contracts;\n"),
            ("worker/Contracts.cs", "namespace Orders.Contracts { public record OrderCreated(Guid OrderId); }\nnamespace Hidden { public record OrderCreated(int N); }\n"),
            ("worker/Consumer.cs", "namespace Worker;\npublic class C : IConsumer<OrderCreated> { }\n"));

        Assert.Equal("message:Orders.Contracts.OrderCreated", result.Fact("subscription:worker:Worker.C:Orders.Contracts.OrderCreated").Attr("message"));
        Assert.Equal(["message:Orders.Contracts.OrderCreated"], result.Facts.Where(f => f.Kind == FactKind.Message).Select(f => f.Key));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_contract_field_named_like_a_credential_is_a_name_and_stays_in_the_field_list()
    {
        var result = Api("""
            using MassTransit;
            namespace Shop.Api;
            public record Login(string User, string Token, string ApiKey = "none");
            public class Session
            {
                public string Password { get; init; }
                public Guid Id { get; init; }
            }
            public class C(IBus bus)
            {
                public Task Run() => bus.Publish(new Login("u", "t"));
                public Task Open() => bus.Send(new Session());
            }
            """);

        Assert.Equal("ApiKey:string;Token:string;User:string", result.Fact("message:Shop.Api.Login").Attr("fields"));
        Assert.Equal("Id:Guid;Password:string", result.Fact("message:Shop.Api.Session").Attr("fields"));
        Assert.Empty(result.Notes);
    }

    // ---- channels (review item 16) ----

    private const string BusSource = """
        using MassTransit;
        namespace Shop.Api;
        public record OrderPlaced(Guid OrderId);
        public class PlacedConsumer : IConsumer<OrderPlaced> { }
        public static class Bus
        {
            public static void Configure(IRabbitMqBusFactoryConfigurator cfg, IBusRegistrationContext context)
            {
                cfg.Message<OrderPlaced>(m => m.SetEntityName("order-events"));
                cfg.ReceiveEndpoint("shop.order-placed", e =>
                {
                    e.ConfigureConsumer<PlacedConsumer>(context);
                });
            }
        }
        public class Sender(IPublishEndpoint publisher)
        {
            public Task Go() => publisher.Publish(new OrderPlaced(Guid.NewGuid()));
        }
        """;

    private static string ProjectWith(params string[] packages) =>
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup>"
        + string.Concat(packages.Select(p => $"<PackageReference Include=\"{p}\" Version=\"8.3.4\" />"))
        + "</ItemGroup></Project>";

    [Fact]
    public void A_queue_and_an_entity_name_are_channels_of_the_broker_of_the_one_transport_package()
    {
        var result = Api(BusSource);

        Assert.Equal(["channel:amqp:order-events", "channel:amqp:shop.order-placed"], result.Facts.Where(f => f.Kind == FactKind.Channel).Select(f => f.Key));
        var exchange = result.Fact("channel:amqp:order-events");
        Assert.Equal("order-events", exchange.Name);
        // The same attributes, with the same values, that the Go rules give an exchange of this name.
        Assert.Equal(("exchange", "amqp", "message:Shop.Api.OrderPlaced"), (exchange.Attr("kind"), exchange.Attr("transport"), exchange.Attr("entityOf")));
        Assert.Equal(["src/Shop.Api/Code.cs:9-9"], exchange.Lines());
        Assert.Equal(("dotnet.masstransit-entity-name", 2), (exchange.Evidence[0].Rule, exchange.Evidence[0].RuleVersion));
        var queue = result.Fact("channel:amqp:shop.order-placed");
        Assert.Equal(("queue", "amqp"), (queue.Attr("kind"), queue.Attr("transport")));
        Assert.Equal(["src/Shop.Api/Code.cs:10-13"], queue.Lines());

        var subscription = Assert.Single(result.Facts, f => f.Kind == FactKind.Subscription);
        Assert.Equal("subscription:src/Shop.Api:Shop.Api.PlacedConsumer:Shop.Api.OrderPlaced", subscription.Key);
        Assert.Equal(("channel:amqp:shop.order-placed", "message:Shop.Api.OrderPlaced"), (subscription.Attr("queue"), subscription.Attr("message")));
        Assert.Equal(["src/Shop.Api/Code.cs:4-4", "src/Shop.Api/Code.cs:10-13"], subscription.Lines());
        Assert.Equal(
            [("dotnet.masstransit-consumer", 2), ("dotnet.masstransit-receive-endpoint", 2)],
            subscription.Evidence.Select(e => (e.Rule, e.RuleVersion)));

        var publication = Assert.Single(result.Facts, f => f.Kind == FactKind.Publication);
        Assert.Equal(("publication:src/Shop.Api:Shop.Api.OrderPlaced", "channel:amqp:order-events"), (publication.Key, publication.Attr("channel")));
        Assert.Equal(["src/Shop.Api/Code.cs:18-18"], publication.Lines());
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("masstransit", null, "MassTransit")]
    [InlineData("masstransit", null, "MassTransit.RabbitMQ", "MassTransit.Kafka")]
    [InlineData("azure-service-bus", "topic", "MassTransit.Azure.ServiceBus.Core")]
    [InlineData("kafka", "topic", "MassTransit.Kafka")]
    [InlineData("amazon-sqs", "topic", "MassTransit.AmazonSQS", "MassTransit.EntityFrameworkCore")]
    public void The_broker_of_a_channel_is_not_guessed_when_the_project_has_no_single_transport_package(string broker, string? kind, params string[] packages)
    {
        var result = Api(BusSource, ProjectWith(packages));

        Assert.Equal([$"channel:{broker}:order-events", $"channel:{broker}:shop.order-placed"], result.Facts.Where(f => f.Kind == FactKind.Channel).Select(f => f.Key));
        var entity = result.Fact($"channel:{broker}:order-events");
        Assert.Equal((kind, broker), (entity.Attr("kind"), entity.Attr("transport")));
        Assert.Equal($"channel:{broker}:shop.order-placed", result.Facts.Single(f => f.Kind == FactKind.Subscription).Attr("queue"));
    }

    [Fact]
    public void A_receive_endpoint_inside_another_is_noted_and_its_consumer_gets_no_queue()
    {
        var result = Api("""
            using MassTransit;
            public record M(int N);
            public class Outer : IConsumer<M> { }
            public class Inner : IConsumer<M> { }
            public static class Bus
            {
                public static void Configure(IBusFactoryConfigurator cfg, IBusRegistrationContext context)
                {
                    cfg.ReceiveEndpoint("outer", e =>
                    {
                        cfg.ReceiveEndpoint("inner", x => x.ConfigureConsumer<Inner>(context));
                        e.ConfigureConsumer<Outer>(context);
                    });
                }
            }
            """);

        Assert.Equal(["channel:amqp:outer"], result.Facts.Where(f => f.Kind == FactKind.Channel).Select(f => f.Key));
        Assert.Equal("channel:amqp:outer", result.Fact("subscription:src/Shop.Api:Outer:M").Attr("queue"));
        Assert.Null(result.Fact("subscription:src/Shop.Api:Inner:M").Attr("queue"));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "nested-receive-endpoint", "src/Shop.Api/Code.cs", 11)], result.Notes);
    }

    // ---- outbox look-alike (review item 10) ----

    [Fact]
    public void A_set_of_outbox_settings_with_a_hosted_service_that_reads_it_and_publishes_is_not_an_outbox()
    {
        var result = Repo.Extract(
            ("Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker" />"""),
            ("Shop/ShopDb.cs", """
                public class ShopDb : DbContext
                {
                    public DbSet<OutboxSettings> Outbox { get; set; }
                    public DbSet<OutboxArchive> OutboxArchive { get; set; }
                }
                """),
            ("Shop/Job.cs", """
                public sealed class OutboxRetentionJob(ShopDb db, IBus bus) : BackgroundService
                {
                    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        var settings = await db.Outbox.FirstAsync(stoppingToken);
                        await db.OutboxArchive.Where(a => a.At < settings.Cutoff).ExecuteDeleteAsync(stoppingToken);
                        await bus.Publish(new RetentionDone(), stoppingToken);
                    }
                }
                """));

        Assert.Equal(["dataaccess:Shop:ShopDb", "service:Shop"], result.Facts.Select(f => f.Key));
    }

    [Theory]
    [InlineData("Outbox", true)]
    [InlineData("OutboxMessages", true)]
    [InlineData("OutboxEvent", true)]
    [InlineData("OutboxEntries", true)]
    [InlineData("IntegrationEventLogEntry", true)]
    [InlineData("OrderOutboxMessageRow", true)]
    [InlineData("MyIntegrationEventLogs", true)]
    [InlineData("OutboxSettings", false)]
    [InlineData("OutboxState", false)]
    [InlineData("MessageOutboxLog", false)]
    [InlineData("outboxmessage", false)]
    public void The_hand_written_outbox_rule_knows_an_outbox_entity_by_its_type_name(string entity, bool isOutbox)
    {
        var result = Repo.Extract(
            ("Shop/Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker" />"""),
            ("Shop/ShopDb.cs", $"namespace Shop;\npublic class ShopDb : DbContext {{ public DbSet<{entity}> Pending {{ get; set; }} }}\n"),
            ("Shop/Dispatcher.cs", """
                namespace Shop;
                public sealed class Dispatcher(IDbContextFactory<ShopDb> factory, IModel channel) : IHostedService
                {
                    public async Task StartAsync(CancellationToken ct)
                    {
                        await using var db = await factory.CreateDbContextAsync(ct);
                        foreach (var row in db.Pending)
                            channel.BasicPublish("events", "", null, row.Body);
                    }
                }
                """));

        Assert.Equal(isOutbox, result.Has("outbox:Shop:hand-written"));
        if (isOutbox)
        {
            var outbox = result.Fact("outbox:Shop:hand-written");
            Assert.Equal((entity, "ShopDb", "Dispatcher"), (outbox.Attr("entity"), outbox.Attr("dbContext"), outbox.Attr("dispatcher")));
            Assert.Equal(["Shop/Dispatcher.cs:2-3", "Shop/Dispatcher.cs:7-7", "Shop/Dispatcher.cs:8-8", "Shop/ShopDb.cs:2-2"], outbox.Lines());
        }
    }

    // ---- bounded work (review item 17) ----

    private const int Bound = 262_000;
    private const string BusProject = """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="MassTransit" Version="8.3.4" /></ItemGroup></Project>""";

    private static string Fill(string prefix, string unit, string suffix = "")
    {
        var count = (Bound - prefix.Length - suffix.Length) / unit.Length;
        return prefix + string.Concat(Enumerable.Repeat(unit, count)) + suffix;
    }

    private static string Repeat(string unit, int length) => string.Concat(Enumerable.Repeat(unit, length / unit.Length));

    /// <summary>An opener and a closer, each repeated the same number of times, so every bracket is closed.</summary>
    private static string Nest(string prefix, string open, string middle, string close, string suffix = "")
    {
        var count = (Bound - prefix.Length - middle.Length - suffix.Length) / (open.Length + close.Length);
        return prefix + string.Concat(Enumerable.Repeat(open, count)) + middle + string.Concat(Enumerable.Repeat(close, count)) + suffix;
    }

    private static string Numbered(string prefix, Func<int, string> unit, string suffix = "", int share = 1)
    {
        var builder = new System.Text.StringBuilder(prefix);
        var limit = prefix.Length + (Bound - prefix.Length - suffix.Length) / share;
        for (var k = 1; ; k++)
        {
            var next = unit(k);
            if (builder.Length + next.Length > limit)
                break;
            builder.Append(next);
        }
        return builder.Append(suffix).ToString();
    }

    private const string Controller = "[ApiController] class A : ControllerBase { ";

    /// <summary>Every input is one C# file at the size bound. The first five are the inputs of the review.</summary>
    internal static readonly (string Name, Func<string> Content)[] HostileInputs =
    [
        ("dollars", () => Fill("", "$")),
        ("hole-of-dollars", () => Fill("var s = $\"{", "$")),
        ("attribute-groups", () => Fill(Controller, "[A] x ", " }")),
        ("verb-attributes", () => Fill(Controller, "[HttpGet] x ", " }")),
        ("nested-endpoints", () => Nest("using MassTransit; ", "x.ReceiveEndpoint(\"q\", e => { e.ConfigureConsumer<C>(x); ", "", "});")),
        ("interpolation-openers", () => Fill("", "$\"{")),
        ("interpolation-openers-on-lines", () => Fill("", "$\"{\n")),
        ("raw-interpolation", () => Fill("", "$$\"\"\"{{ ")),
        ("verbatim-dollars", () => Fill("", "@$$$$$$$ ")),
        ("quotes", () => Fill("", "\"")),
        ("nested-attribute-arguments", () => Nest(Controller, "[HttpGet(", "", ")]", " public void M() { } }")),
        ("attribute-flood-on-one-action", () => Fill(Controller, "[HttpGet(\"b\")]", " public void M() { } }")),
        ("route-flood-on-class-and-action", () =>
            "[ApiController] " + Repeat("[Route(\"a\")]", Bound / 2) + " class A : ControllerBase { " + Repeat("[HttpGet(\"b\")]", Bound / 2 - 100) + " public void M() { } }"),
        ("route-product", () => Fill(
            "[ApiController] [Route(\"r1\")][Route(\"r2\")][Route(\"r3\")][Route(\"r4\")][Route(\"r5\")][Route(\"r6\")][Route(\"r7\")][Route(\"r8\")] class A : ControllerBase { ",
            "[HttpGet(\"a\")][HttpGet(\"b\")][HttpGet(\"c\")][HttpGet(\"d\")][HttpPut(\"a\")][HttpPut(\"b\")][HttpPut(\"c\")][HttpPut(\"d\")] public void M() { } ", "}")),
        ("derived-controllers", () =>
            Numbered("[ApiController] [Route(\"a\")] abstract class B : ControllerBase { ", k => $"[HttpGet(\"x{k}\")] public void M{k}() {{ }} ", "} ", share: 2)
            + Numbered("", k => $"class D{k} : B {{ }} ", share: 2)),
        ("nested-types", () => Nest("", "class A { ", "", "}")),
        ("type-headers", () => Fill("", "class A : B ")),
        ("attributes-before-types", () => Fill("", "[A]", " record a ; record b ; record c ;")),
        ("builder-assignments", () => Fill("var b = WebApplication.CreateBuilder(args); ", "a = b.Build(); a.MapGet(\"/x\", h); ")),
        ("variable-chain", () => Numbered("var a0 = WebApplication.Create(args); ", k => $"var a{k} = a{k - 1}.MapGroup(\"x\"); a{k}.MapGet(\"/y\", h); ")),
        ("group-chain", () => Fill("WebApplication.Create(args)", ".MapGroup(\"\")", ".MapGet(\"/y\", h);")),
        ("long-route-prefix", () => Fill("var a = WebApplication.Create(args).MapGroup(\"" + new string('x', 100_000) + "\"); ", "a.MapGet(\"/y\", h); ")),
        ("routes", () => Numbered("var a = WebApplication.Create(args); ", k => $"a.MapGet(\"/{k}\", h); ")),
        ("nested-lambdas", () => Nest("var app = WebApplication.Create(args); ", "f(app => ", "app.MapGet(\"/x\", h)", ")", ";")),
        ("nested-parentheses", () => Nest("", "(", "var x = y;", ")")),
        ("preprocessor-nesting", () => Fill("var x = WebApplication.Create(args);\n", "#if A\nx.MapGet(\"/a\", h);\n")),
        ("preprocessor-elif-chain", () => Fill("#if A\n", "#elif B\nx();\n")),
        ("preprocessor-conditions", () => Numbered("var x = WebApplication.Create(args);\n", k => $"#if A{k}\nx.MapGet(\"/a\", h);\n#endif\n")),
        ("publish-sites", () => Fill("using MassTransit; class P(IBus bus) { void M() { ", "bus.Publish(new M()); ", "} }")),
        ("bus-declarations", () => Fill("using MassTransit; ", "IBus x; x.Publish(new M()); ")),
        ("consumers-times-endpoints", () =>
            Numbered("using MassTransit; class C : IDisposable", k => $", IConsumer<M{k}>", " { } ", share: 2)
            + Repeat("x.ReceiveEndpoint(\"q\", e => e.ConfigureConsumer<C>(y)); ", Bound / 2 - 100)),
        ("same-named-types", () =>
            Numbered("using MassTransit; ", k => $"namespace N{k} {{ class M {{ }} }} ", share: 2) + Numbered("", k => $"class C{k} : IConsumer<M> {{ }} ", share: 2)),
        ("long-namespace", () => Fill("using MassTransit; namespace " + new string('n', 100_000) + "; ", "class A : IConsumer<A> { } ")),
        ("usings", () => Fill("", "using a.b; ")),
        ("entity-names", () => Fill("using MassTransit; ", "x.Message<M>(m => m.SetEntityName(\"e\")); x.Publish(new M()); ")),
        ("outbox-tables-times-dispatchers", () =>
            Numbered("class Db : DbContext { ", k => $"public DbSet<OutboxMessage> O{k} {{ get; set; }} ", "} ", share: 2)
            + Numbered("", k => $"class H{k}(Db db, IBus bus) : BackgroundService {{ void M() {{ db.O{k}.ToList(); bus.Publish(new X()); }} }} ", share: 2)),
        ("record-parameters", () => Fill("using MassTransit; class P(IBus bus) { void M() { bus.Publish(new R()); } } record R(", "int A, ", "int Z);")),
        ("properties", () => Fill("using MassTransit; class P(IBus bus) { void M() { bus.Publish(new R()); } } class R { ", "public int A { get; } ", "}")),
    ];

    public static TheoryData<string> HostileNames() => new(HostileInputs.Select(i => i.Name));

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void A_hostile_source_file_at_the_size_bound_is_read_in_bounded_work(string name)
    {
        var content = HostileInputs.Single(i => i.Name == name).Content();
        Assert.InRange(content.Length, Bound - 4096, ExtractionLimits.Default.MaxFileBytes);

        var (result, work) = Repo.ExtractBounded("Api/Code.cs", content, ("Api/Api.csproj", BusProject));

        // The work is counted: an input that costs nothing was not read by a metered loop.
        Assert.True(work >= content.Length, $"{name}: {work} operations for {content.Length} characters");
        Assert.True(result.Facts.Count <= ExtractionLimits.Default.MaxFactsPerFile + 4, $"{name}: {result.Facts.Count} facts");
    }

    [Fact]
    public void Constructs_that_nest_deeper_than_the_documented_depth_are_noted_and_not_read()
    {
        var types = Api(string.Concat(Enumerable.Repeat("class A { ", 20)) + "class Db : DbContext { }" + new string('}', 20));
        Assert.DoesNotContain(types.Facts, f => f.Kind == FactKind.DataAccess);
        Assert.Contains(types.Notes, n => n is { Kind: NoteKind.Undecided, Reason: "type-nesting" });

        var conditions = Api(
            "var app = WebApplication.Create(args);\n" + string.Concat(Enumerable.Repeat("#if A\n", 40)) + "app.MapGet(\"/deep\", () => 1);\n"
            + string.Concat(Enumerable.Repeat("#endif\n", 40)) + "app.MapGet(\"/after\", () => 1);\n");
        Assert.Equal(["operation:src/Shop.Api:GET /after"], Operations(conditions));
        Assert.Null(conditions.Fact("operation:src/Shop.Api:GET /after").Attr("conditional"));
        Assert.Contains(conditions.Notes, n => n is { Kind: NoteKind.Undecided, Reason: "preprocessor-nesting", Line: 34 });

        var chain = Api(
            "var a0 = WebApplication.Create(args);\n"
            + string.Concat(Enumerable.Range(1, 40).Select(k => $"var a{k} = a{k - 1}.MapGroup(\"g\");\n")) + "a40.MapGet(\"/x\", () => 1);\na3.MapGet(\"/y\", () => 1);\n");
        Assert.Equal(["operation:src/Shop.Api:GET /g/g/g/y"], Operations(chain));
        Assert.Contains(chain.Notes, n => n is { Kind: NoteKind.Undecided, Reason: "route-prefix-depth" });
        Assert.Contains(chain.Notes, n => n is { Kind: NoteKind.Undecided, Reason: "unresolved-route-prefix", Line: 42 });
    }
}
