using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The relationships a code scan derives from the facts of the other extractors, and the two
/// .NET rules that give them evidence: database client registrations and HTTP client base
/// addresses. The orders fixture pins message flow, data access and depends_on as a whole
/// (<see cref="OrdersFixtureTests"/>); these are the edges of the rules.
/// </summary>
public class RelationshipExtractorTests
{
    private const string Csproj = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    private const string GoMod = "module github.com/acme/shop/{0}\n\ngo 1.23\n";
    private const string GoMain = "package main\n\nfunc main() {}\n";

    private static IEnumerable<ExtractedFact> Relationships(ExtractionResult result) =>
        result.Facts.Where(f => f.Kind == FactKind.Relationship);

    private static string Edge(ExtractedFact f) => $"{f.Attr("from")} -> {f.Attr("to")}: {f.Attr("label")}";

    [Fact]
    public void Db_client_registrations_state_the_store_kind_and_the_connection_they_read()
    {
        var result = Repo.Extract(
            ("api/Api.csproj", Csproj),
            ("api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddDbContext<ShopDb>(o =>
                    o.UseSqlServer(builder.Configuration.GetConnectionString("Shop")));
                builder.AddNpgsqlDataSource("ledger");
                var cache = ConnectionMultiplexer.Connect(builder.Configuration["Redis:Endpoint"]);
                using var raw = new NpgsqlConnection(builder.Configuration["ConnectionStrings:Audit"]);
                """));

        var shop = result.Fact("dataaccess:api:client:ShopDb");
        Assert.Equal(("sqlserver", "Shop", null, "ShopDb", "efcore", "service:api"),
            (shop.Attr("storeKind"), shop.Attr("connectionName"), shop.Attr("connectionVariable"), shop.Attr("dbContext"), shop.Attr("technology"), shop.Attr("service")));
        Assert.Equal(["api/Program.cs:2-3"], shop.Lines());
        Assert.Equal(("dotnet.db-client", 1), (shop.Evidence[0].Rule, shop.Evidence[0].RuleVersion));

        Assert.Equal(("postgres", "ledger"), (result.Fact("dataaccess:api:client:ledger").Attr("storeKind"), result.Fact("dataaccess:api:client:ledger").Attr("connectionName")));
        var redis = result.Fact("dataaccess:api:client:Redis:Endpoint");
        Assert.Equal(("redis", "Redis:Endpoint"), (redis.Attr("storeKind"), redis.Attr("connectionVariable")));
        Assert.Equal(("postgres", "Audit"), (result.Fact("dataaccess:api:client:Audit").Attr("storeKind"), result.Fact("dataaccess:api:client:Audit").Attr("connectionName")));
    }

    [Fact]
    public void A_go_service_states_the_variable_its_database_connection_reads()
    {
        var result = Repo.Extract(
            ("stock/go.mod", string.Format(GoMod, "stock")),
            ("stock/main.go", """
                package main

                import (
                    "database/sql"
                    "os"
                )

                func main() {
                    db, _ := sql.Open("pgx", os.Getenv("STOCK_DB_URL"))
                    other, _ := sql.Open("pgx", os.Getenv(name))
                }
                """));

        Assert.Equal("STOCK_DB_URL", result.Fact("dataaccess:stock:sql:pgx").Attr("connectionVariable"));
    }

    [Fact]
    public void The_variable_the_code_reads_names_the_store_among_several()
    {
        var result = Repo.Extract(
            ("docker-compose.yml", """
                services:
                  main-db:
                    image: postgres:16
                  reports-db:
                    image: postgres:16
                  api:
                    build: ./api
                    environment:
                      ConnectionStrings__Shop: Host=main-db;Database=shop;Username=app
                      Reports: postgres://app@reports-db:5432/reports
                    depends_on: [main-db, reports-db]
                """),
            ("api/Api.csproj", Csproj),
            ("api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddDbContext<ShopDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Shop")));
                """));

        var reads = Assert.Single(Relationships(result), f => f.Attr("label") == "reads and writes");
        Assert.Equal("service:api -> datastore:.:main-db: reads and writes", Edge(reads));
        Assert.Equal("relationship:data-access:service:api->datastore:.:main-db", reads.Key);
        Assert.Equal("behavioural", reads.Attr("relationshipKind"));
        // The registration line and the variable that names the host, with the two ends.
        Assert.Contains("api/Program.cs:2-2", reads.Lines());
        Assert.Contains("docker-compose.yml:9-9", reads.Lines());
        // No value of a variable enters a fact.
        Assert.DoesNotContain(result.Facts.SelectMany(f => f.Attributes.Values), v => v.Contains("Username=app", StringComparison.Ordinal));
        Assert.Equal(
            ["service:api -> datastore:.:main-db: depends on", "service:api -> datastore:.:reports-db: depends on"],
            Relationships(result).Where(f => f.Attr("label") == "depends on").Select(Edge).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void With_no_one_store_to_name_there_is_no_relationship_and_a_note()
    {
        var result = Repo.Extract(
            ("docker-compose.yml", """
                services:
                  a-db:
                    image: postgres:16
                  b-db:
                    image: postgres:16
                  stock:
                    build: ./stock
                    depends_on: [a-db, b-db]
                """),
            ("stock/go.mod", string.Format(GoMod, "stock")),
            ("stock/main.go", """
                package main

                import "database/sql"

                func main() {
                    db, _ := sql.Open("pgx", dsn)
                }
                """));

        Assert.DoesNotContain(Relationships(result), f => f.Attr("label") == "reads and writes");
        Assert.Contains(result.Notes, n => n.Reason == "data-access-store-not-resolved" && n.Path == "stock/main.go" && n.Line == 6);
        Assert.Equal(2, Relationships(result).Count(f => f.Attr("label") == "depends on"));
    }

    [Theory]
    [InlineData("PAYMENTS_URL")]
    [InlineData("PAYMENTS__BASEURL")]
    [InlineData("Services__Payments__Endpoint")]
    public void A_variable_that_names_an_app_is_an_inferred_call_with_its_confidence(string variable)
    {
        var result = Repo.Extract(
            ("docker-compose.yml", $$"""
                services:
                  rabbitmq:
                    image: rabbitmq:3
                  cache:
                    image: redis:7
                  payments:
                    build: ./payments
                  orders:
                    build: ./orders
                    environment:
                      - {{variable}}=http://payments:8080
                      - RABBITMQ_URL=amqp://rabbitmq
                      - CACHE_HOST=cache:6379
                      - ORDERS_URL=http://orders:8080
                """),
            ("payments/go.mod", string.Format(GoMod, "payments")), ("payments/main.go", GoMain),
            ("orders/go.mod", string.Format(GoMod, "orders")), ("orders/main.go", GoMain));

        // A broker, a store and the service itself are no HTTP call.
        var call = Assert.Single(Relationships(result));
        Assert.Equal(("relationship:http-call:service:orders->service:payments", "calls", "behavioural"), (call.Key, call.Attr("label"), call.Attr("relationshipKind")));
        Assert.Equal(("inferred", "0.6", variable), (call.Attr("truthLabel"), call.Attr("confidence"), call.Attr("variables")));
        Assert.Contains("docker-compose.yml:11-11", call.Lines());
        Assert.Equal("relationship.http-call", call.Evidence.Single(e => e.StartLine == 11).Rule);
    }

    [Fact]
    public void An_http_client_whose_base_address_reads_the_variable_raises_the_confidence()
    {
        var result = Repo.Extract(
            ("docker-compose.yml", """
                services:
                  payments:
                    build: ./payments
                  api:
                    build: ./api
                    environment:
                      Payments__BaseUrl: http://payments:8080
                """),
            ("payments/go.mod", string.Format(GoMod, "payments")), ("payments/main.go", GoMain),
            ("api/Api.csproj", Csproj),
            ("api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddHttpClient("payments", client =>
                {
                    client.BaseAddress = new Uri(builder.Configuration["Payments:BaseUrl"]!);
                });
                """));

        var client = result.Fact("dependency:api:http-client:Payments:BaseUrl");
        Assert.Equal(("http-client", "Payments:BaseUrl", "service:api"), (client.Attr("role"), client.Attr("baseAddressKey"), client.Attr("service")));
        Assert.Equal(["api/Program.cs:2-5"], client.Lines());

        var call = Assert.Single(Relationships(result));
        Assert.Equal(("service:api -> service:payments: calls", "inferred", "0.8"), (Edge(call), call.Attr("truthLabel"), call.Attr("confidence")));
        Assert.Contains("api/Program.cs:2-5", call.Lines());
        Assert.Contains("docker-compose.yml:7-7", call.Lines());
    }

    [Fact]
    public void Two_services_that_fit_a_name_equally_give_no_call()
    {
        var result = Repo.Extract(
            ("docker-compose.yml", """
                services:
                  payments:
                    build: ./payments
                  payments-api:
                    build: ./payments-api
                  orders:
                    build: ./orders
                    environment:
                      - PAYMENTS_API_URL=http://payments-api
                      - PAYMENTS_HOST_URL=http://x
                """),
            ("payments/go.mod", string.Format(GoMod, "payments")), ("payments/main.go", GoMain),
            ("payments-api/go.mod", string.Format(GoMod, "payments-api")), ("payments-api/main.go", GoMain),
            ("orders/go.mod", string.Format(GoMod, "orders")), ("orders/main.go", GoMain));

        // The longest name wins: payments-api + URL, not payments + API URL. "HOST URL" is no address word.
        Assert.Equal(["service:orders -> service:payments-api: calls"], Relationships(result).Select(Edge));
    }

    [Fact]
    public void A_consumer_reached_through_a_binding_with_another_key_is_not_one()
    {
        var result = Repo.Extract(
            ("shipping/go.mod", string.Format(GoMod, "shipping")),
            ("shipping/main.go", """
                package main

                import amqp "github.com/rabbitmq/amqp091-go"

                func main() {}

                func consume(ch *amqp.Channel) error {
                    if err := ch.ExchangeDeclare("orders", "topic", true, false, false, false, nil); err != nil {
                        return err
                    }
                    if _, err := ch.QueueDeclare("shipping.paid", true, false, false, false, nil); err != nil {
                        return err
                    }
                    if err := ch.QueueBind("shipping.paid", "order.paid", "orders", false, nil); err != nil {
                        return err
                    }
                    deliveries, err := ch.Consume("shipping.paid", "shipping", false, false, false, false, nil)
                    _ = deliveries
                    return err
                }
                """),
            ("orders/Orders.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="MassTransit.RabbitMQ" Version="8.3.4" /></ItemGroup></Project>"""),
            ("orders/OrderPlaced.cs", "namespace Orders;\npublic record OrderPlaced(System.Guid Id);\n"),
            ("orders/Program.cs", """
                using MassTransit;
                using Orders;
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddMassTransit(bus => bus.UsingRabbitMq((context, cfg) =>
                    cfg.Message<OrderPlaced>(m => m.SetEntityName("orders"))));
                var app = builder.Build();
                app.MapPost("/orders", async (IPublishEndpoint publisher) => await publisher.Publish(new OrderPlaced(System.Guid.NewGuid())));
                app.Run();
                """));

        // The publication has no routing key, and the binding has one: the trace engine routes nothing there.
        Assert.NotNull(result.Fact("publication:orders:Orders.OrderPlaced"));
        Assert.NotNull(result.Fact("binding:shipping:amqp:shipping.paid:orders:order.paid"));
        Assert.DoesNotContain(Relationships(result), f => f.Attr("label") == "publishes OrderPlaced");
    }
}
