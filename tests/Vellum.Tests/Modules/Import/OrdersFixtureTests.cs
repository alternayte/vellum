using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The Orders repositories are the reference case of the product. The whole result is pinned
/// by a committed file that a reviewer can read next to the sources, and the facts of the
/// hero demonstration are asserted one by one with the text that stands at their lines.
/// </summary>
public class OrdersFixtureTests
{
    private static readonly ExtractionInput Input = Repo.Load("orders");
    private static readonly ExtractionResult Result = EvidenceExtractor.Extract(Input);

    [Theory]
    [InlineData("orders")]
    [InlineData("orders-outbox-removed")]
    public void The_whole_result_equals_the_committed_expected_facts(string repository)
    {
        var actual = ExtractionJson.Serialize(EvidenceExtractor.Extract(Repo.Load(repository)));

        // To accept a deliberate change: set VELLUM_WRITE_EXPECTED_FACTS to the directory
        // tests/Vellum.Tests/Fixtures/Repos, run this test, and review the diff.
        if (Environment.GetEnvironmentVariable("VELLUM_WRITE_EXPECTED_FACTS") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, $"{repository}.expected-facts.json"), actual);

        var expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Repos", $"{repository}.expected-facts.json");
        Assert.True(File.Exists(expectedPath), $"Missing {expectedPath}");
        Assert.Equal(File.ReadAllText(expectedPath).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void The_scan_is_complete_and_has_nothing_to_report()
    {
        Assert.True(Result.IsComplete);
        Assert.Empty(Result.Notes);
    }

    [Fact]
    public void The_three_services_are_found_and_the_contracts_library_is_not_a_service()
    {
        Assert.Equal(
            ["service:billing", "service:inventory-worker", "service:orders-api"],
            Result.Facts.Where(f => f.Kind == FactKind.Service).Select(f => f.Key));
        Assert.Equal("web", Result.Fact("service:orders-api").Attr("kind"));
        Assert.Equal("worker", Result.Fact("service:billing").Attr("kind"));
        Assert.Equal("true", Result.Fact("service:inventory-worker").Attr("hasMain"));
    }

    [Fact]
    public void Post_orders_is_an_operation_of_orders_api_at_the_lines_of_its_handler()
    {
        var operation = Result.Fact("operation:orders-api:POST /orders");

        Assert.Equal(FactKind.Operation, operation.Kind);
        Assert.Equal("POST /orders", operation.Name);
        Assert.Equal("service:orders-api", operation.Attr("service"));
        Assert.Equal("POST", operation.Attr("method"));
        Assert.Equal("/orders", operation.Attr("path"));
        Assert.Equal("http", operation.Attr("transport"));
        Assert.Equal(["orders-api/Program.cs:40-50"], operation.Lines());
        Assert.Equal(
            $"github.com/acme/orders@{Repo.Sha}:orders-api/Program.cs:40-50",
            operation.Evidence[0].Pointer(Result.Repository, Result.CommitSha));

        var text = Input.Text("orders-api/Program.cs", 40, 50);
        Assert.StartsWith("orders.MapPost(\"/\", async (CreateOrder request", text);
        Assert.EndsWith("});", text);
    }

    [Fact]
    public void Get_order_resolves_the_group_prefix_and_drops_the_route_constraint()
    {
        var operation = Result.Fact("operation:orders-api:GET /orders/{id}");

        Assert.Equal("/orders/{id:guid}", operation.Attr("routeTemplate"));
        Assert.Equal(["orders-api/Program.cs:52-53"], operation.Lines());
        Assert.StartsWith("orders.MapGet(\"/{id:guid}\"", Input.Text("orders-api/Program.cs", 52, 53));
    }

    [Fact]
    public void A_route_in_a_comment_or_in_a_string_is_not_an_operation()
    {
        // orders-api/Program.cs holds "// app.MapPost("/v0/orders" …" and a string with
        // app.MapGet("/orders/search"). inventory-worker/main.go holds a commented
        // "POST /reservations" and a raw string with "GET /debug/stock".
        Assert.Equal(
            [
                "operation:inventory-worker:GET /healthz",
                "operation:openapi.yaml:GET /healthz",
                "operation:openapi.yaml:GET /orders/{id}",
                "operation:openapi.yaml:POST /orders",
                "operation:orders-api:GET /healthz",
                "operation:orders-api:GET /orders/{id}",
                "operation:orders-api:POST /orders",
            ],
            Result.Facts.Where(f => f.Kind == FactKind.Operation).Select(f => f.Key));
    }

    [Fact]
    public void OrderCreated_is_one_message_with_its_contract_and_its_payload_schema()
    {
        var message = Result.Fact("message:OrderCreated");

        Assert.Equal(FactKind.Message, message.Kind);
        Assert.Equal("Orders.Contracts.OrderCreated", message.Attr("clrType"));
        Assert.Equal("CreatedAt:DateTimeOffset;CustomerId:Guid;OrderId:Guid;Total:decimal", message.Attr("fields"));
        Assert.Equal("schema:asyncapi.yaml#/components/schemas/OrderCreated", message.Attr("payloadSchema"));
        Assert.Equal(["asyncapi.yaml:32-37", "contracts/OrderCreated.cs:7-11"], message.Lines());
        Assert.StartsWith("public sealed record OrderCreated(", Input.Text("contracts/OrderCreated.cs", 7, 11));
        Assert.StartsWith("    OrderCreated:", Input.Text("asyncapi.yaml", 32, 37));

        var schema = Result.Fact("schema:asyncapi.yaml#/components/schemas/OrderCreated");
        Assert.Contains("\"total\":{\"type\":\"number\"}", schema.Attr("schema"));
    }

    [Fact]
    public void Orders_api_publishes_OrderCreated_to_the_order_events_exchange()
    {
        var publication = Result.Fact("publication:orders-api:OrderCreated");

        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal("service:orders-api", publication.Attr("service"));
        Assert.Equal("message:OrderCreated", publication.Attr("message"));
        Assert.Equal("channel:order-events", publication.Attr("channel"));
        Assert.Equal(["orders-api/Program.cs:46-46"], publication.Lines());
        Assert.Contains("publisher.Publish(new OrderCreated(", Input.Text("orders-api/Program.cs", 46, 46));

        var exchange = Result.Fact("channel:order-events");
        Assert.Equal("exchange", exchange.Attr("kind"));
        Assert.Equal("fanout", exchange.Attr("amqp.exchange.type"));
        Assert.Equal("message:OrderCreated", exchange.Attr("entityOf"));
        // Three sources agree on the exchange: the AsyncAPI document, the Go declaration, the .NET entity name.
        Assert.Equal(
            ["asyncapi.yaml:10-21", "inventory-worker/consumer.go:33-33", "orders-api/Program.cs:25-25"],
            exchange.Lines());
        Assert.DoesNotContain(Result.Notes, n => n.Kind == NoteKind.Conflict);
    }

    [Fact]
    public void The_outbox_is_a_fact_with_the_registration_and_the_tables_as_evidence()
    {
        var outbox = Result.Fact("outbox:orders-api:masstransit-entity-framework");

        Assert.Equal(FactKind.Publication, outbox.Kind);
        Assert.Equal("transactional-outbox", outbox.Attr("pattern"));
        Assert.Equal("service:orders-api", outbox.Attr("service"));
        Assert.Equal("OrdersDbContext", outbox.Attr("dbContext"));
        Assert.Equal("true", outbox.Attr("busOutbox"));
        Assert.Equal("true", outbox.Attr("outboxEntities"));
        Assert.Equal(
            [
                "orders-api/OrdersDbContext.cs:20-20", "orders-api/OrdersDbContext.cs:21-21", "orders-api/OrdersDbContext.cs:22-22",
                "orders-api/Program.cs:16-20", "orders-api/Program.cs:19-19",
            ],
            outbox.Lines());
        Assert.StartsWith("    bus.AddEntityFrameworkOutbox<OrdersDbContext>(outbox =>", Input.Text("orders-api/Program.cs", 16, 20));
        Assert.Contains("outbox.UseBusOutbox();", Input.Text("orders-api/Program.cs", 19, 19));
        Assert.Contains("modelBuilder.AddOutboxMessageEntity();", Input.Text("orders-api/OrdersDbContext.cs", 20, 20));
    }

    [Fact]
    public void Billing_consumes_OrderCreated_from_its_queue()
    {
        var subscription = Result.Fact("subscription:billing:OrderCreatedConsumer:OrderCreated");

        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal("service:billing", subscription.Attr("service"));
        Assert.Equal("message:OrderCreated", subscription.Attr("message"));
        Assert.Equal("channel:billing.order-created", subscription.Attr("queue"));
        Assert.Equal(["billing/OrderCreatedConsumer.cs:7-9", "billing/Program.cs:16-20"], subscription.Lines());
        Assert.Contains(": IConsumer<OrderCreated>", Input.Text("billing/OrderCreatedConsumer.cs", 7, 9));
        Assert.StartsWith("        cfg.ReceiveEndpoint(\"billing.order-created\"", Input.Text("billing/Program.cs", 16, 20));

        Assert.Equal("queue", Result.Fact("channel:billing.order-created").Attr("kind"));
    }

    [Fact]
    public void Inventory_worker_consumes_its_queue_which_is_bound_to_the_exchange_and_dead_letters()
    {
        var subscription = Result.Fact("subscription:inventory-worker:amqp:inventory.order-created");
        Assert.Equal("service:inventory-worker", subscription.Attr("service"));
        Assert.Equal("channel:inventory.order-created", subscription.Attr("queue"));
        Assert.Equal(["inventory-worker/consumer.go:50-50"], subscription.Lines());
        Assert.Contains("ch.Consume(orderCreatedQueue,", Input.Text("inventory-worker/consumer.go", 50, 50));

        var binding = Result.Fact("binding:inventory.order-created:order-events");
        Assert.Equal(FactKind.Subscription, binding.Kind);
        Assert.Equal("channel:inventory.order-created", binding.Attr("queue"));
        Assert.Equal("channel:order-events", binding.Attr("exchange"));
        Assert.Equal(["inventory-worker/consumer.go:46-46"], binding.Lines());

        var queue = Result.Fact("channel:inventory.order-created");
        Assert.Equal("queue", queue.Attr("kind"));
        Assert.Equal("channel:order-events.dlx", queue.Attr("deadLetterExchange"));
        Assert.Equal(["inventory-worker/consumer.go:40-42"], queue.Lines());
        Assert.Contains("\"x-dead-letter-exchange\": deadLetterExchange,", Input.Text("inventory-worker/consumer.go", 40, 42));

        var deadLetters = Result.Fact("channel:order-events.dlx");
        Assert.Equal("exchange", deadLetters.Attr("kind"));
        Assert.Equal(["inventory-worker/consumer.go:36-36"], deadLetters.Lines());
        Assert.Contains("ch.ExchangeDeclare(deadLetterExchange, \"fanout\"", Input.Text("inventory-worker/consumer.go", 36, 36));
    }

    [Fact]
    public void Data_stores_and_data_access_are_found()
    {
        Assert.Equal("postgres", Result.Fact("datastore:postgres").Attr("kind"));
        Assert.Equal("POSTGRES_DB,POSTGRES_PASSWORD,POSTGRES_USER", Result.Fact("datastore:postgres").Attr("environment"));

        var context = Result.Fact("dataaccess:orders-api:OrdersDbContext");
        Assert.Equal("Order", context.Attr("entities"));
        Assert.Equal("postgres", context.Attr("storeKind"));
        Assert.Equal("postgres", Result.Fact("dataaccess:inventory-worker:sql:pgx").Attr("storeKind"));
    }

    [Fact]
    public void The_pull_request_removes_the_outbox_and_changes_the_OrderCreated_contract_and_nothing_else()
    {
        var head = EvidenceExtractor.Extract(Repo.Load("orders-outbox-removed"));
        Assert.True(head.IsComplete);
        Assert.Empty(head.Notes);

        var delta = FactDiff.Compare(Result.Facts, head.Facts);

        Assert.Empty(delta.Added);
        Assert.Equal(["outbox:orders-api:masstransit-entity-framework"], delta.Removed.Select(f => f.Key));
        Assert.Equal(
            ["message:OrderCreated|fields", "schema:asyncapi.yaml#/components/schemas/OrderCreated|schema"],
            delta.Changed.Select(c => $"{c.Key}|{string.Join(",", c.Attributes)}"));

        Assert.Equal(
            "CreatedAt:DateTimeOffset;CustomerId:Guid;OrderId:Guid;TotalAmount:decimal",
            head.Fact("message:OrderCreated").Attr("fields"));
        Assert.Contains("\"totalAmount\":{\"type\":\"number\"}", head.Fact("schema:asyncapi.yaml#/components/schemas/OrderCreated").Attr("schema"));

        // The publication is still there: the event is still published, now without the outbox.
        // Its lines moved; its key and attributes did not.
        var publication = head.Fact("publication:orders-api:OrderCreated");
        Assert.Equal(["orders-api/Program.cs:38-38"], publication.Lines());
        Assert.Equal(Result.Fact("publication:orders-api:OrderCreated").Attributes, publication.Attributes);
        Assert.Equal(Result.Facts.Count - 1, head.Facts.Count);
    }
}
