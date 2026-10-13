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

        Assert.Equal("/orders/{id}", operation.Attr("path"));
        Assert.Equal("/orders/{}", operation.Attr("routeTemplate"));
        Assert.Equal("/orders/{id:guid}", operation.Attr("declaredRoute"));
        // The OpenAPI document names the parameter the same way here; the template is what matches when it does not.
        Assert.Equal(operation.Attr("routeTemplate"), Result.Fact("operation:openapi.yaml:GET /orders/{id}").Attr("routeTemplate"));
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
    public void OrderCreated_is_a_code_contract_and_a_document_entry_that_do_not_share_a_key()
    {
        var contract = Result.Fact("message:Orders.Contracts.OrderCreated");
        Assert.Equal(FactKind.Message, contract.Kind);
        Assert.Equal("OrderCreated", contract.Name);
        Assert.Equal("Orders.Contracts.OrderCreated", contract.Attr("clrType"));
        Assert.Equal("contracts/Orders.Contracts.csproj", contract.Attr("declaredIn"));
        Assert.Equal("CreatedAt:DateTimeOffset;CustomerId:Guid;OrderId:Guid;Total:decimal", contract.Attr("fields"));
        Assert.Equal(["contracts/OrderCreated.cs:7-11"], contract.Lines());
        Assert.StartsWith("public sealed record OrderCreated(", Input.Text("contracts/OrderCreated.cs", 7, 11));

        // The AsyncAPI entry is another claim about the same event. Nothing merges the two.
        var entry = Result.Fact("message:asyncapi.yaml#OrderCreated");
        Assert.Equal(FactKind.Message, entry.Kind);
        Assert.Equal("schema:asyncapi.yaml#/components/schemas/OrderCreated", entry.Attr("payloadSchema"));
        Assert.Null(entry.Attr("fields"));
        Assert.Equal(["asyncapi.yaml:32-37"], entry.Lines());
        Assert.StartsWith("    OrderCreated:", Input.Text("asyncapi.yaml", 32, 37));
        Assert.False(Result.Has("message:OrderCreated"));

        var schema = Result.Fact("schema:asyncapi.yaml#/components/schemas/OrderCreated");
        Assert.Contains("\"total\":{\"type\":\"number\"}", schema.Attr("schema"));
    }

    [Fact]
    public void The_go_worker_reads_OrderCreated_through_the_structs_it_decodes_a_delivery_into()
    {
        var envelope = Result.Fact("message:github.com/acme/orders/inventory-worker.envelope");
        Assert.Equal("message:orderCreated;messageId:string", envelope.Attr("fields"));
        Assert.Equal(["inventory-worker/consumer.go:26-29", "inventory-worker/consumer.go:63-63"], envelope.Lines());
        Assert.StartsWith("type envelope struct {", Input.Text("inventory-worker/consumer.go", 26, 29));
        Assert.Contains("json.Unmarshal(delivery.Body, &event)", Input.Text("inventory-worker/consumer.go", 63, 63));

        var payload = Result.Fact("message:github.com/acme/orders/inventory-worker.orderCreated");
        Assert.Equal("customerId:string;orderId:string;total:float64", payload.Attr("fields"));
        Assert.Equal(["inventory-worker/consumer.go:19-23"], payload.Lines());
        Assert.StartsWith("type orderCreated struct {", Input.Text("inventory-worker/consumer.go", 19, 23));
    }

    [Fact]
    public void Orders_api_publishes_OrderCreated_to_the_order_events_exchange()
    {
        var publication = Result.Fact("publication:orders-api:Orders.Contracts.OrderCreated");

        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal("service:orders-api", publication.Attr("service"));
        Assert.Equal("message:Orders.Contracts.OrderCreated", publication.Attr("message"));
        Assert.Equal("channel:amqp:order-events", publication.Attr("channel"));
        Assert.Equal(["orders-api/Program.cs:46-46"], publication.Lines());
        Assert.Contains("publisher.Publish(new OrderCreated(", Input.Text("orders-api/Program.cs", 46, 46));

        var exchange = Result.Fact("channel:amqp:order-events");
        Assert.Equal("exchange", exchange.Attr("kind"));
        Assert.Equal("fanout", exchange.Attr("amqp.exchange.type"));
        Assert.Equal("message:Orders.Contracts.OrderCreated", exchange.Attr("entityOf"));
        // Two code sources agree on the exchange: the Go declaration and the .NET entity name.
        Assert.Equal(["inventory-worker/consumer.go:33-33", "orders-api/Program.cs:25-25"], exchange.Lines());
        Assert.DoesNotContain(exchange.Attributes.Keys, name => name.StartsWith("conflict.", StringComparison.Ordinal));
        Assert.DoesNotContain(Result.Notes, n => n.Kind == NoteKind.Conflict);

        // The AsyncAPI channel is a document entry with its own key. It names the same address.
        var declared = Result.Fact("channel:asyncapi.yaml#orderEvents");
        Assert.Equal(("order-events", "fanout"), (declared.Attr("address"), declared.Attr("amqp.exchange.type")));
        Assert.Equal(["asyncapi.yaml:10-21"], declared.Lines());
    }

    [Fact]
    public void The_outbox_is_a_fact_with_the_registration_and_the_tables_as_evidence()
    {
        var outbox = Result.Fact("outbox:orders-api:masstransit-entity-framework");

        // A pattern of the service, not a publication.
        Assert.Equal(FactKind.Pattern, outbox.Kind);
        Assert.Equal(["outbox:orders-api:masstransit-entity-framework"], Result.Facts.Where(f => f.Kind == FactKind.Pattern).Select(f => f.Key));
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
        var subscription = Result.Fact("subscription:billing:Billing.OrderCreatedConsumer:Orders.Contracts.OrderCreated");

        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal("service:billing", subscription.Attr("service"));
        Assert.Equal("message:Orders.Contracts.OrderCreated", subscription.Attr("message"));
        Assert.Equal("channel:amqp:billing.order-created", subscription.Attr("queue"));
        Assert.Equal(["billing/OrderCreatedConsumer.cs:7-9", "billing/Program.cs:16-20"], subscription.Lines());
        Assert.Contains(": IConsumer<OrderCreated>", Input.Text("billing/OrderCreatedConsumer.cs", 7, 9));
        Assert.StartsWith("        cfg.ReceiveEndpoint(\"billing.order-created\"", Input.Text("billing/Program.cs", 16, 20));

        Assert.Equal("queue", Result.Fact("channel:amqp:billing.order-created").Attr("kind"));
    }

    [Fact]
    public void Inventory_worker_consumes_its_queue_which_is_bound_to_the_exchange_and_dead_letters()
    {
        var subscription = Result.Fact("subscription:inventory-worker:amqp:inventory.order-created");
        Assert.Equal("service:inventory-worker", subscription.Attr("service"));
        Assert.Equal("channel:amqp:inventory.order-created", subscription.Attr("queue"));
        Assert.Equal("message:github.com/acme/orders/inventory-worker.envelope", subscription.Attr("message"));
        Assert.Equal(["inventory-worker/consumer.go:50-50", "inventory-worker/consumer.go:63-63"], subscription.Lines());
        Assert.Contains("ch.Consume(orderCreatedQueue,", Input.Text("inventory-worker/consumer.go", 50, 50));

        var binding = Result.Fact("binding:inventory-worker:amqp:inventory.order-created:order-events");
        Assert.Equal(FactKind.Subscription, binding.Kind);
        Assert.Equal("channel:amqp:inventory.order-created", binding.Attr("queue"));
        Assert.Equal("channel:amqp:order-events", binding.Attr("exchange"));
        Assert.Equal(["inventory-worker/consumer.go:46-46"], binding.Lines());

        var queue = Result.Fact("channel:amqp:inventory.order-created");
        Assert.Equal("queue", queue.Attr("kind"));
        Assert.Equal("channel:amqp:order-events.dlx", queue.Attr("deadLetterExchange"));
        Assert.Equal(["inventory-worker/consumer.go:40-42"], queue.Lines());
        Assert.Contains("\"x-dead-letter-exchange\": deadLetterExchange,", Input.Text("inventory-worker/consumer.go", 40, 42));

        var deadLetters = Result.Fact("channel:amqp:order-events.dlx");
        Assert.Equal("exchange", deadLetters.Attr("kind"));
        Assert.Equal(["inventory-worker/consumer.go:36-36"], deadLetters.Lines());
        Assert.Contains("ch.ExchangeDeclare(deadLetterExchange, \"fanout\"", Input.Text("inventory-worker/consumer.go", 36, 36));
    }

    [Fact]
    public void Data_stores_and_data_access_are_found()
    {
        Assert.Equal("postgres", Result.Fact("datastore:.:postgres").Attr("kind"));
        Assert.Equal("POSTGRES_DB,POSTGRES_PASSWORD,POSTGRES_USER", Result.Fact("datastore:.:postgres").Attr("environment"));

        var context = Result.Fact("dataaccess:orders-api:Orders.Api.OrdersDbContext");
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

        // Exactly three things: the outbox is gone, the field of OrderCreated has a new name (in the
        // .NET contract and in the struct the Go worker decodes), and the AsyncAPI schema follows it.
        Assert.Empty(delta.Added);
        Assert.Equal(["Pattern outbox:orders-api:masstransit-entity-framework"], delta.Removed.Select(f => $"{f.Kind} {f.Key}"));
        Assert.Equal(
            [
                "message:Orders.Contracts.OrderCreated|fields",
                "message:github.com/acme/orders/inventory-worker.orderCreated|fields",
                "schema:asyncapi.yaml#/components/schemas/OrderCreated|schema",
            ],
            delta.Changed.Select(c => $"{c.Key}|{string.Join(",", c.Attributes)}"));

        Assert.Equal(
            "CreatedAt:DateTimeOffset;CustomerId:Guid;OrderId:Guid;TotalAmount:decimal",
            head.Fact("message:Orders.Contracts.OrderCreated").Attr("fields"));
        Assert.Equal(
            "customerId:string;orderId:string;totalAmount:float64",
            head.Fact("message:github.com/acme/orders/inventory-worker.orderCreated").Attr("fields"));
        Assert.Contains("\"totalAmount\":{\"type\":\"number\"}", head.Fact("schema:asyncapi.yaml#/components/schemas/OrderCreated").Attr("schema"));
        Assert.DoesNotContain(head.Facts, f => f.Kind == FactKind.Pattern);

        // The publication is still there: the event is still published, now without the outbox.
        // Its lines moved; its key and attributes did not.
        var publication = head.Fact("publication:orders-api:Orders.Contracts.OrderCreated");
        Assert.Equal(["orders-api/Program.cs:38-38"], publication.Lines());
        Assert.Equal(Result.Fact("publication:orders-api:Orders.Contracts.OrderCreated").Attributes, publication.Attributes);
        Assert.Equal(Result.Facts.Count - 1, head.Facts.Count);
    }
}
