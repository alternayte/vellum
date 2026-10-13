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

        Assert.Equal(["service:seeder", "service:shop-api"], result.Facts.Where(f => f.Kind == FactKind.Service).Select(f => f.Key));
        var service = result.Fact("service:shop-api");
        Assert.Equal("Shop.Api", service.Name);
        Assert.Equal("web", service.Attr("kind"));
        Assert.Equal("net10.0", service.Attr("targetFramework"));
        Assert.Equal(["src/Shop.Api/Shop.Api.csproj:1-1"], service.Lines());
        Assert.Equal("executable", result.Fact("service:seeder").Attr("kind"));

        var kafka = result.Fact("dependency:shop-api:nuget:Confluent.Kafka");
        Assert.Equal(FactKind.Dependency, kafka.Kind);
        Assert.Equal(("kafka", "messaging", "kafka", "2.6.0"), (kafka.Attr("technology"), kafka.Attr("role"), kafka.Attr("broker"), kafka.Attr("version")));
        Assert.Equal(["src/Shop.Api/Shop.Api.csproj:6-6"], kafka.Lines());
        Assert.Equal("dapper", result.Fact("dependency:shop-api:nuget:Dapper").Attr("technology"));
        Assert.Equal("refit", result.Fact("dependency:shop-api:nuget:Refit.HttpClientFactory").Attr("technology"));
        Assert.Equal("rabbitmq", result.Fact("dependency:shop-api:nuget:RabbitMQ.Client").Attr("technology"));
        // An unknown package is a dependency with no claim about what it is.
        var serilog = result.Fact("dependency:shop-api:nuget:Serilog");
        Assert.Null(serilog.Attr("technology"));
        Assert.Null(serilog.Attr("version"));
        Assert.Equal("../Shop.Domain/Shop.Domain.csproj", result.Fact("dependency:shop-api:project:Shop.Domain").Attr("reference"));
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
                "operation:shop-api:DELETE /api/v1/admin/items/{rest}",
                "operation:shop-api:GET /",
                "operation:shop-api:PATCH /reports/{id}",
                "operation:shop-api:PUT /api/v1/items/{sku}",
            ],
            Operations(result));

        var put = result.Fact("operation:shop-api:PUT /api/v1/items/{sku}");
        Assert.Equal("/api/v1/items/{sku:alpha}", put.Attr("routeTemplate"));
        Assert.Equal("aspnetcore-minimal-api", put.Attr("framework"));
        Assert.Equal("service:shop-api", put.Attr("service"));
        Assert.Equal(["src/Shop.Api/Code.cs:7-7"], put.Lines());
        Assert.Equal(["src/Shop.Api/Code.cs:10-12"], result.Fact("operation:shop-api:PATCH /reports/{id}").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_route_in_a_comment_or_in_any_kind_of_string_is_not_extracted()
    {
        var result = Api(""""
            var app = builder.Build();
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

        Assert.Equal(["operation:shop-api:GET /real"], Operations(result));
        // The line number after every multi-line literal is still exact.
        Assert.Equal(["src/Shop.Api/Code.cs:20-20"], result.Fact("operation:shop-api:GET /real").Lines());
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

        Assert.Equal(["operation:shop-api:GET /known"], Operations(result));
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
            var app = builder.Build();
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
                "operation:shop-api:DELETE /api/Orders/purge",
                "operation:shop-api:GET /api/Orders",
                "operation:shop-api:GET /api/Orders/{id}",
                "operation:shop-api:GET /ping",
                "operation:shop-api:POST /api/Orders/Cancel",
                "operation:shop-api:PUT /absolute/{id}",
            ],
            Operations(result));

        var get = result.Fact("operation:shop-api:GET /api/Orders/{id}");
        Assert.Equal("aspnetcore-controller", get.Attr("framework"));
        Assert.Equal("OrdersController", get.Attr("controller"));
        Assert.Equal("Get", get.Attr("action"));
        Assert.Equal("api/Orders/{id:guid}", get.Attr("routeTemplate"));
        // From the first attribute to the end of the parameter list.
        Assert.Equal(["src/Shop.Api/Code.cs:10-14"], get.Lines());
        Assert.Equal(["src/Shop.Api/Code.cs:7-8"], result.Fact("operation:shop-api:GET /api/Orders").Lines());
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
            ["subscription:shop-api:OrderPlacedConsumer:IStockLow", "subscription:shop-api:OrderPlacedConsumer:OrderPlaced"],
            result.Facts.Where(f => f.Kind == FactKind.Subscription).Select(f => f.Key));
        var consumer = result.Fact("subscription:shop-api:OrderPlacedConsumer:OrderPlaced");
        Assert.Equal("message:OrderPlaced", consumer.Attr("message"));
        Assert.Null(consumer.Attr("queue"));
        Assert.Equal(["src/Shop.Api/Code.cs:15-16"], consumer.Lines());

        Assert.Equal(
            ["publication:shop-api:IStockLow", "publication:shop-api:ShipOrder"],
            result.Facts.Where(f => f.Kind == FactKind.Publication).Select(f => f.Key));
        Assert.Equal("publish", result.Fact("publication:shop-api:IStockLow").Attr("mode"));
        Assert.Equal(["src/Shop.Api/Code.cs:22-22"], result.Fact("publication:shop-api:IStockLow").Lines());
        Assert.Equal("send", result.Fact("publication:shop-api:ShipOrder").Attr("mode"));

        // The contracts of the three message types that a consumer or a publish names.
        var placed = result.Fact("message:OrderPlaced");
        Assert.Equal("Shop.Api.OrderPlaced", placed.Attr("clrType"));
        Assert.Equal("OrderId:Guid;Sku:string", placed.Attr("fields"));
        Assert.Equal(["src/Shop.Api/Code.cs:5-5"], placed.Lines());
        Assert.Equal("Remaining:int;Sku:string", result.Fact("message:IStockLow").Attr("fields"));
        Assert.Equal("OrderId:Guid;Skus:List<string>", result.Fact("message:ShipOrder").Attr("fields"));
        Assert.Equal(["src/Shop.Api/Code.cs:7-13"], result.Fact("message:ShipOrder").Lines());
        Assert.False(result.Has("message:NotABusMessage"));

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

        Assert.Equal(["service:shop-api"], result.Facts.Select(f => f.Key));
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

        Assert.Null(result.Fact("subscription:shop-api:C:Thing").Attr("queue"));
        Assert.DoesNotContain(result.Facts, f => f.Kind == FactKind.Channel);
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-queue-name", "src/Shop.Api/Code.cs", 7)], result.Notes);
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

        var transactional = result.Fact("outbox:shop-api:masstransit-entity-framework");
        Assert.Equal("ShopDb", transactional.Attr("dbContext"));
        Assert.Equal("false", transactional.Attr("busOutbox"));
        Assert.Equal("false", transactional.Attr("outboxEntities"));
        Assert.Equal(["src/Shop.Api/Code.cs:6-6"], transactional.Lines());
        var inMemory = result.Fact("outbox:shop-api:masstransit-in-memory");
        Assert.Equal("in-memory-outbox", inMemory.Attr("pattern"));
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
                    var pending = await db.Outbox.Where(m => m.SentAt == null).ToListAsync(stoppingToken);
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
        var outbox = both.Fact("outbox:shop:hand-written");
        Assert.Equal(FactKind.Publication, outbox.Kind);
        Assert.Equal(
            ("transactional-outbox", "hand-written", "ShopDb", "OutboxMessage", "OutboxDispatcher"),
            (outbox.Attr("pattern"), outbox.Attr("mechanism"), outbox.Attr("dbContext"), outbox.Attr("entity"), outbox.Attr("dispatcher")));
        Assert.Equal(["Shop/OutboxDispatcher.cs:1-2", "Shop/ShopDb.cs:4-4"], outbox.Lines());

        var tableOnly = Repo.Extract(("Shop/Shop.csproj", PlainProject), ("Shop/ShopDb.cs", Context), ("Shop/CacheWarmer.cs", Unrelated));
        Assert.False(tableOnly.Has("outbox:shop:hand-written"));

        var dispatcherOnly = Repo.Extract(("Shop/Shop.csproj", PlainProject), ("Shop/OutboxDispatcher.cs", Dispatcher));
        Assert.False(dispatcherOnly.Has("outbox:shop:hand-written"));
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

        var access = result.Fact("dataaccess:shop-api:ShopDb");
        Assert.Equal(FactKind.DataAccess, access.Kind);
        Assert.Equal("Dictionary<string,Tag>,Product", access.Attr("entities"));
        Assert.Equal("sqlserver", access.Attr("storeKind"));
        Assert.Equal(["src/Shop.Api/Code.cs:1-2", "src/Shop.Api/Code.cs:3-3", "src/Shop.Api/Code.cs:4-4"], access.Lines());
        Assert.False(result.Has("dataaccess:shop-api:NotAContext"));
    }

    [Fact]
    public void The_store_is_unknown_when_no_provider_or_two_providers_are_referenced()
    {
        const string Source = "public class Db : DbContext { public DbSet<A> As { get; set; } }";

        Assert.Equal("unknown", Api(Source, """<Project Sdk="Microsoft.NET.Sdk.Web" />""").Fact("dataaccess:shop-api:Db").Attr("storeKind"));
        Assert.Equal("unknown", Api(Source, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.2" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
              </ItemGroup>
            </Project>
            """).Fact("dataaccess:shop-api:Db").Attr("storeKind"));
    }
}
