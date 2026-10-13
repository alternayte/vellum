using System.Text;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>OpenAPI, AsyncAPI and the YAML/JSON reader, in the forms the Orders fixture does not use.</summary>
public class ContractExtractorTests
{
    private static (string, int)[] Rules(ExtractedFact fact) =>
        fact.Evidence.Select(e => (e.Rule, e.RuleVersion)).Distinct().ToArray();

    private const string OpenApiJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Stock", "version": "2.0.0" },
          "servers": [
            { "url": "https://stock.acme.example/api" }
          ],
          "paths": {
            "/items/{sku}": {
              "put": {
                "operationId": "putItem",
                "security": [ { "oauth": [ "write" ] } ],
                "requestBody": {
                  "content": {
                    "application/xml": { "schema": { "type": "object" } },
                    "application/json": { "schema": { "$ref": "#/components/schemas/Item" } }
                  }
                },
                "responses": {
                  "204": { "description": "Stored." },
                  "default": { "content": { "application/problem+json": { "schema": { "$ref": "common.yaml#/Problem" } } } }
                }
              },
              "delete": { "deprecated": true, "responses": {} }
            },
            "/shared": { "$ref": "paths.yaml#/shared" }
          },
          "components": {
            "schemas": {
              "Item": {
                "type": "object",
                "properties": { "sku": { "type": "string" }, "count": { "type": "integer", "example": 3 } },
                "x-internal": true
              }
            }
          }
        }
        """;

    [Fact]
    public void OpenApi_json_gives_operations_schemas_and_the_document_with_exact_lines()
    {
        var result = Repo.Extract(("api/openapi.json", OpenApiJson));

        var put = result.Fact("operation:api/openapi.json:PUT /items/{sku}");
        Assert.Equal(FactKind.Operation, put.Kind);
        Assert.Equal("PUT /items/{sku}", put.Name);
        Assert.Equal("putItem", put.Attr("operationId"));
        Assert.Equal("required", put.Attr("security"));
        Assert.Equal(("http", "PUT"), (put.Attr("transport"), put.Attr("method")));
        Assert.Equal(("/items/{sku}", "/items/{}", "/items/{sku}"), (put.Attr("path"), put.Attr("routeTemplate"), put.Attr("declaredRoute")));
        Assert.Equal("application/json", put.Attr("requestContentType"));
        Assert.Equal("schema:api/openapi.json#/components/schemas/Item", put.Attr("requestSchema"));
        Assert.Equal("204,default", put.Attr("responses"));
        Assert.Equal("external-ref", put.Attr("response.default.schema"));
        Assert.Equal("contract:api/openapi.json", put.Attr("contract"));
        Assert.Equal(["api/openapi.json:9-22"], put.Lines());
        Assert.Equal([("openapi.operation", 2)], Rules(put));

        var delete = result.Fact("operation:api/openapi.json:DELETE /items/{sku}");
        Assert.Equal("true", delete.Attr("deprecated"));
        Assert.Equal("none", delete.Attr("security"));
        Assert.Equal(["api/openapi.json:23-23"], delete.Lines());

        var schema = result.Fact("schema:api/openapi.json#/components/schemas/Item");
        Assert.Equal(FactKind.Schema, schema.Kind);
        Assert.Equal(("contract:api/openapi.json", "json-schema", "#/components/schemas/Item"), (schema.Attr("contract"), schema.Attr("format"), schema.Attr("pointer")));
        // Keys sorted; the example and the extension field are left out.
        Assert.Equal(
            """{"properties":{"count":{"type":"integer"},"sku":{"type":"string"}},"type":"object"}""",
            schema.Attr("schema"));
        Assert.Equal(["api/openapi.json:29-33"], schema.Lines());
        Assert.Equal([("openapi.schema", 2)], Rules(schema));

        var document = result.Fact("contract:api/openapi.json");
        Assert.Equal(("Stock", "openapi", "3.1.0", "Stock", "2.0.0"), (document.Name, document.Attr("format"), document.Attr("specVersion"), document.Attr("title"), document.Attr("apiVersion")));
        Assert.Equal("https://stock.acme.example/api", document.Attr("servers"));
        Assert.Equal(["api/openapi.json:2-2", "api/openapi.json:4-6"], document.Lines());
        Assert.Equal([("openapi.document", 2)], Rules(document));

        // A path item behind a reference to another file is not followed and not guessed.
        Assert.DoesNotContain(result.Facts, f => f.Key.Contains("/shared"));
        var note = Assert.Single(result.Notes);
        Assert.Equal(new ExtractionNote(NoteKind.Undecided, "external-ref", "api/openapi.json", 25), note);
    }

    private const string OpenApiYaml = """
        openapi: 3.0.3
        info:
          title: Orders
          version: 1.4.0
        security:
          - apiKey: []
        paths:
          /orders/{orderId:guid}/:
            get:
              operationId: getOrder
              deprecated: True
              security:
                - {}
                - apiKey: []
              responses:
                '200':
                  content:
                    application/json:
                      schema:
                        $ref: '#/components/schemas/Order'
                '404':
                  content:
                    application/json:
                      schema:
                        $ref: '#/components/schemas/Missing'
            post:
              operationId: getOrder
              security: []
              responses: {}
            delete:
              responses: {}
          /alias:
            $ref: '#/x-items/shared'
          /loop:
            $ref: '#/x-items/a'
          /gone:
            $ref: '#/x-items/none'
        x-items:
          shared:
            $ref: '#/x-items/target'
          target:
            put:
              responses: {}
          a:
            $ref: '#/x-items/b'
          b:
            $ref: '#/x-items/a'
        components:
          schemas:
            Order:
              type: object
        """;

    [Fact]
    public void OpenApi_reads_optional_security_boolean_spellings_and_local_references()
    {
        var result = Repo.Extract(("openapi.yaml", OpenApiYaml));

        // "- {}" allows anonymous access beside the key: the operation does not require security.
        var get = result.Fact("operation:openapi.yaml:GET /orders/{orderId}");
        Assert.Equal("optional", get.Attr("security"));
        Assert.Equal("true", get.Attr("deprecated"));
        Assert.Equal(("/orders/{orderId}", "/orders/{}", "/orders/{orderId:guid}/"), (get.Attr("path"), get.Attr("routeTemplate"), get.Attr("declaredRoute")));
        Assert.Equal("schema:openapi.yaml#/components/schemas/Order", get.Attr("response.200.schema"));
        // The schema of the 404 response is not in the document: no key is made up for it.
        Assert.Null(get.Attr("response.404.schema"));
        Assert.Equal("200,404", get.Attr("responses"));
        Assert.Equal(["openapi.yaml:9-25"], get.Lines());

        // An empty list at the operation removes the global requirement.
        var post = result.Fact("operation:openapi.yaml:POST /orders/{orderId}");
        Assert.Equal("none", post.Attr("security"));
        Assert.Equal("getOrder", post.Attr("operationId"));
        Assert.Null(post.Attr("deprecated"));
        Assert.Equal(["openapi.yaml:26-29"], post.Lines());

        var delete = result.Fact("operation:openapi.yaml:DELETE /orders/{orderId}");
        Assert.Equal("required", delete.Attr("security"));
        Assert.Equal(["openapi.yaml:30-31"], delete.Lines());

        // A path item behind a local reference is read where it is defined, two hops away.
        var put = result.Fact("operation:openapi.yaml:PUT /alias");
        Assert.Equal("required", put.Attr("security"));
        Assert.Equal(["openapi.yaml:32-33", "openapi.yaml:42-43"], put.Lines());

        Assert.Equal(
            ["contract:openapi.yaml", "operation:openapi.yaml:DELETE /orders/{orderId}", "operation:openapi.yaml:GET /orders/{orderId}",
             "operation:openapi.yaml:POST /orders/{orderId}", "operation:openapi.yaml:PUT /alias", "schema:openapi.yaml#/components/schemas/Order"],
            result.Facts.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["openapi.yaml:1-1"], result.Fact("contract:openapi.yaml").Lines());
        Assert.Equal(["openapi.yaml:50-51"], result.Fact("schema:openapi.yaml#/components/schemas/Order").Lines());
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "dangling-ref", "openapi.yaml", 25),
                new ExtractionNote(NoteKind.Undecided, "duplicate-operation-id", "openapi.yaml", 27),
                new ExtractionNote(NoteKind.Undecided, "ref-cycle", "openapi.yaml", 35),
                new ExtractionNote(NoteKind.Undecided, "dangling-ref", "openapi.yaml", 37),
            ],
            result.Notes);
    }

    [Fact]
    public void Two_paths_that_normalise_to_one_path_are_a_conflict_and_no_spelling_wins()
    {
        var result = Repo.Extract(("o.yaml", """
            openapi: 3.0.0
            paths:
              /a/{id}:
                get:
                  responses: {}
              /a/{id}/:
                get:
                  responses: {}
            """));

        var get = result.Fact("operation:o.yaml:GET /a/{id}");
        Assert.Equal("/a/{id}", get.Attr("path"));
        Assert.Null(get.Attr("declaredRoute"));
        Assert.Equal("/a/{id} @ o.yaml:4-5 | /a/{id}/ @ o.yaml:7-8", get.Attr("conflict.declaredRoute"));
        Assert.Equal(["o.yaml:4-5", "o.yaml:7-8"], get.Lines());
        Assert.Equal([new ExtractionNote(NoteKind.Conflict, "attribute:declaredRoute", "o.yaml", 4)], result.Notes);
    }

    [Fact]
    public void Server_urls_never_carry_user_information_with_or_without_a_scheme()
    {
        var result = Repo.Extract(
            ("openapi.yaml", """
                openapi: 3.0.0
                servers:
                  - url: //admin:hunter2@api.example.com/v1
                  - url: admin:hunter2@plain.example.com
                  - url: https://u:p@ss/word@host/x
                  - url: https://ok.example.com:8443/base?key=hunter2
                paths: {}
                """),
            ("v2/asyncapi.yaml", """
                asyncapi: 2.6.0
                servers:
                  broker:
                    url: amqp://guest:hunter2@rabbit.example.com:5672/vhost
                    protocol: amqp
                  bad:
                    url: amqp://u:p@ss/word@host/x
                    protocol: amqp
                channels: {}
                """),
            ("v3/asyncapi.yaml", """
                asyncapi: 3.0.0
                servers:
                  main:
                    host: admin:hunter2@kafka.example.com:9092
                    pathname: /prod
                    protocol: kafka
                  bad:
                    host: "u:p@ss/word@host"
                    protocol: kafka
                """));

        Assert.Equal(
            "//api.example.com/v1,plain.example.com,https://ok.example.com:8443/base",
            result.Fact("contract:openapi.yaml").Attr("servers"));
        var v2 = result.Fact("contract:v2/asyncapi.yaml");
        Assert.Equal(("bad,broker", "amqp", "amqp://rabbit.example.com:5672/vhost"), (v2.Attr("servers"), v2.Attr("protocols"), v2.Attr("serverUrls")));
        var v3 = result.Fact("contract:v3/asyncapi.yaml");
        Assert.Equal(("bad,main", "kafka", "kafka.example.com:9092/prod"), (v3.Attr("servers"), v3.Attr("protocols"), v3.Attr("serverUrls")));

        // A URL that cannot be parsed safely gives nothing for that server, and a note.
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "server-url-not-safe", "openapi.yaml", 5),
                new ExtractionNote(NoteKind.Undecided, "server-url-not-safe", "v2/asyncapi.yaml", 6),
                new ExtractionNote(NoteKind.Undecided, "server-url-not-safe", "v3/asyncapi.yaml", 7),
            ],
            result.Notes);
        var json = ExtractionJson.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("p@ss", json);
        Assert.DoesNotContain("admin", json);
    }

    [Fact]
    public void A_schema_body_carries_structure_and_never_values()
    {
        var result = Repo.Extract(("openapi.yaml", """
            openapi: 3.1.0
            paths: {}
            components:
              schemas:
                Account:
                  type: object
                  description: An account.
                  required: [id, default]
                  x-owner: hunter2-team
                  example: {password: hunter2-a}
                  properties:
                    id:
                      type: string
                      format: uuid
                      default: hunter2-b
                      const: hunter2-c
                      examples: [hunter2-d]
                    default:
                      type: string
                      description: "password: hunter2-e"
                    example:
                      type: integer
                    enum:
                      type: string
                      enum: [active, closed]
                    apiKey:
                      type: string
                      enum: [hunter2f]
                    notes:
                      enum: [ok, "hunter2 g with spaces"]
                    nested:
                      allOf:
                        - oneOf:
                            - type: object
                              properties:
                                deep:
                                  anyOf:
                                    - const: hunter2-h
                                      type: string
                                    - default: hunter2-i
                                      x-secret: hunter2-j
                                      items:
                                        - example: hunter2-k
                                          type: number
            """));

        var schema = result.Fact("schema:openapi.yaml#/components/schemas/Account");
        // "default", "example" and "enum" under "properties" are property NAMES and stay; the
        // keywords with those names are values and go, at every depth and inside every combinator.
        Assert.Equal(
            """{"description":"An account.","properties":{"apiKey":{"type":"string"},"default":{"type":"string"},"enum":{"enum":["active","closed"],"type":"string"},"example":{"type":"integer"},"id":{"format":"uuid","type":"string"},"nested":{"allOf":[{"oneOf":[{"properties":{"deep":{"anyOf":[{"type":"string"},{"items":[{"type":"number"}]}]}},"type":"object"}]}]},"notes":{}},"required":["id","default"],"type":"object"}""",
            schema.Attr("schema"));
        Assert.Equal(["openapi.yaml:5-44"], schema.Lines());
        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Redacted, "description-not-emitted", "openapi.yaml", 20),
                new ExtractionNote(NoteKind.Redacted, "enum-not-emitted", "openapi.yaml", 28),
                new ExtractionNote(NoteKind.Redacted, "enum-not-emitted", "openapi.yaml", 30),
            ],
            result.Notes);
    }

    [Fact]
    public void Swagger_2_and_unknown_versions_give_a_note_and_no_fact()
    {
        var result = Repo.Extract(
            ("old.json", """{ "swagger": "2.0", "paths": { "/x": { "get": {} } } }"""),
            ("future.yaml", "openapi: 4.0.0\npaths:\n  /x:\n    get: {}\n"),
            ("events.yaml", "asyncapi: 1.2.0\ntopics: {}\n"));

        Assert.Empty(result.Facts);
        Assert.Equal(
            ["unsupported-asyncapi-version", "unsupported-openapi-version", "unsupported-openapi-version"],
            result.Reasons());
    }

    [Fact]
    public void A_schema_larger_than_the_bound_is_a_fact_without_a_body_and_the_result_is_not_complete()
    {
        var properties = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"property{i}\": {{ \"type\": \"string\" }}"));
        var document = $$"""
            { "openapi": "3.0.3", "paths": {}, "components": { "schemas": {
              "Small": { "type": "string" },
              "Large": { "type": "object", "properties": { {{properties}} } }
            } } }
            """;

        var result = EvidenceExtractor.Extract(Repo.Of(("openapi.json", document)), new ExtractionLimits(MaxSchemaBytes: 1024));

        Assert.False(result.IsComplete);
        Assert.Equal("""{"type":"string"}""", result.Fact("schema:openapi.json#/components/schemas/Small").Attr("schema"));
        Assert.Null(result.Fact("schema:openapi.json#/components/schemas/Large").Attr("schema"));
        Assert.Contains(new ExtractionNote(NoteKind.LimitReached, "limit:schema-bytes", "openapi.json", 3), result.Notes);
    }

    private const string AsyncApiV2 = """
        asyncapi: 2.6.0
        info:
          title: Stock events
          version: 0.3.0
        servers:
          production:
            url: kafka.acme.example:9092
            protocol: kafka
        channels:
          stock.reserved:
            bindings:
              kafka:
                topic: stock.reserved.v1
            subscribe:
              operationId: emitStockReserved
              message:
                $ref: '#/components/messages/StockReserved'
            publish:
              bindings:
                kafka:
                  groupId: auditors
              message:
                oneOf:
                  - $ref: '#/components/messages/StockReserved'
                  - name: StockReleased
                    payload:
                      type: object
                      properties:
                        sku: { type: string }
        components:
          messages:
            StockReserved:
              contentType: application/json
              payload:
                $ref: 'https://schemas.acme.example/stock-reserved.json'
          schemas:
            Sku:
              type: string
              maxLength: 12
        """;

    [Fact]
    public void AsyncApi_2_reads_the_operation_words_from_the_side_of_the_application()
    {
        var result = Repo.Extract(("asyncapi.yaml", AsyncApiV2));

        // The channel name is the channel id in 2.x, and it is the address.
        var channel = result.Fact("channel:asyncapi.yaml#stock.reserved");
        Assert.Equal(FactKind.Channel, channel.Kind);
        Assert.Equal("stock.reserved", channel.Name);
        Assert.Equal("stock.reserved", channel.Attr("address"));
        Assert.Equal("contract:asyncapi.yaml", channel.Attr("contract"));
        Assert.Equal("kafka", channel.Attr("transport"));
        Assert.Equal("topic", channel.Attr("kind"));
        Assert.Equal("stock.reserved.v1", channel.Attr("kafka.topic"));
        Assert.Equal("message:asyncapi.yaml#StockReserved,message:asyncapi.yaml#stock.reserved/StockReleased", channel.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:10-29"], channel.Lines());
        Assert.Equal([("asyncapi.channel", 2)], Rules(channel));

        // "subscribe" in 2.x: others subscribe, so the application publishes.
        var publication = result.Fact("publication:asyncapi.yaml:emitStockReserved");
        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal("subscribe", publication.Attr("action"));
        Assert.Equal("channel:asyncapi.yaml#stock.reserved", publication.Attr("channel"));
        Assert.Equal("message:asyncapi.yaml#StockReserved", publication.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:14-17"], publication.Lines());
        Assert.Equal([("asyncapi.operation", 2)], Rules(publication));

        // No operationId: the key is the channel and the word.
        var subscription = result.Fact("subscription:asyncapi.yaml:stock.reserved:publish");
        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal("auditors", subscription.Attr("kafka.groupId"));
        Assert.Equal("message:asyncapi.yaml#StockReserved,message:asyncapi.yaml#stock.reserved/StockReleased", subscription.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:18-29"], subscription.Lines());

        // The component message takes its id from its key; its payload leaves the file and is not fetched.
        var reserved = result.Fact("message:asyncapi.yaml#StockReserved");
        Assert.Equal(FactKind.Message, reserved.Kind);
        Assert.Equal("StockReserved", reserved.Name);
        Assert.Equal(("application/json", "external-ref"), (reserved.Attr("contentType"), reserved.Attr("payloadSchema")));
        Assert.Null(reserved.Attr("payload"));
        Assert.Equal(["asyncapi.yaml:32-35"], reserved.Lines());
        Assert.Equal([("asyncapi.message", 2)], Rules(reserved));

        // A message written inside an operation has no id of its own: its id starts with the channel.
        var released = result.Fact("message:asyncapi.yaml#stock.reserved/StockReleased");
        Assert.Equal("StockReleased", released.Name);
        Assert.Equal("inline", released.Attr("payloadSchema"));
        Assert.Equal("""{"properties":{"sku":{"type":"string"}},"type":"object"}""", released.Attr("payload"));
        Assert.Equal(["asyncapi.yaml:25-29"], released.Lines());

        var schema = result.Fact("schema:asyncapi.yaml#/components/schemas/Sku");
        Assert.Equal("""{"maxLength":12,"type":"string"}""", schema.Attr("schema"));
        Assert.Equal(["asyncapi.yaml:37-39"], schema.Lines());
        Assert.Equal([("asyncapi.schema", 2)], Rules(schema));

        var document = result.Fact("contract:asyncapi.yaml");
        Assert.Equal(("Stock events", "asyncapi", "2.6.0", "0.3.0"), (document.Name, document.Attr("format"), document.Attr("specVersion"), document.Attr("apiVersion")));
        Assert.Equal(("production", "kafka", "kafka.acme.example:9092"), (document.Attr("servers"), document.Attr("protocols"), document.Attr("serverUrls")));
        Assert.Equal(["asyncapi.yaml:1-1"], document.Lines());
        Assert.Equal([("asyncapi.document", 2)], Rules(document));
        Assert.Empty(result.Notes);

        // A document entry is not the code type of the same name: no key without the file.
        Assert.DoesNotContain(result.Facts, f => f.Key is "message:StockReserved" or "message:StockReleased" or "channel:stock.reserved");
        Assert.Equal(7, result.Facts.Count);
    }

    [Fact]
    public void AsyncApi_3_receive_is_a_subscription_with_amqp_queue_binding_and_routing_keys()
    {
        var result = Repo.Extract(("events/asyncapi.json", """
            {
              "asyncapi": "3.0.0",
              "channels": {
                "audit": {
                  "address": "audit.queue",
                  "messages": { "Audited": { "payload": { "type": "object" } } },
                  "bindings": { "amqp": { "is": "queue", "queue": { "name": "audit.queue" } } }
                }
              },
              "operations": {
                "onAudited": {
                  "action": "receive",
                  "channel": { "$ref": "#/channels/audit" },
                  "bindings": { "amqp": { "cc": ["audit.#", "admin.*"] } }
                },
                "broken": { "action": "send", "channel": { "$ref": "#/channels/missing" } },
                "odd": { "action": "relay", "channel": { "$ref": "#/channels/audit" } }
              }
            }
            """));

        // The key holds the channel id; the broker address is an attribute.
        var channel = result.Fact("channel:events/asyncapi.json#audit");
        Assert.Equal("audit", channel.Name);
        Assert.Equal("audit.queue", channel.Attr("address"));
        Assert.Equal(("amqp", "queue", "queue"), (channel.Attr("transport"), channel.Attr("amqp.is"), channel.Attr("kind")));
        Assert.Equal("audit.queue", channel.Attr("amqp.queue"));
        Assert.Equal("audit", channel.Attr("channelId"));
        Assert.Equal("message:events/asyncapi.json#audit/Audited", channel.Attr("messages"));
        Assert.Equal(["events/asyncapi.json:4-8"], channel.Lines());

        var subscription = result.Fact("subscription:events/asyncapi.json:onAudited");
        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal("receive", subscription.Attr("action"));
        Assert.Equal("channel:events/asyncapi.json#audit", subscription.Attr("channel"));
        // The operation lists no messages, so it carries the messages of its channel.
        Assert.Equal("message:events/asyncapi.json#audit/Audited", subscription.Attr("messages"));
        Assert.Equal("audit.#,admin.*", subscription.Attr("amqp.routingKeys"));
        Assert.Equal(["events/asyncapi.json:11-15"], subscription.Lines());

        var message = result.Fact("message:events/asyncapi.json#audit/Audited");
        Assert.Equal("Audited", message.Name);
        Assert.Equal(("inline", """{"type":"object"}"""), (message.Attr("payloadSchema"), message.Attr("payload")));
        Assert.Equal(["events/asyncapi.json:6-6"], message.Lines());
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "unresolved-channel", "events/asyncapi.json", 16),
                new ExtractionNote(NoteKind.Undecided, "unknown-asyncapi-action", "events/asyncapi.json", 17),
            ],
            result.Notes);
        Assert.DoesNotContain(result.Facts, f => f.Key is "channel:audit.queue" or "message:Audited");
    }

    [Fact]
    public void AsyncApi_3_send_is_a_publication_and_references_end_at_the_component_message()
    {
        var result = Repo.Extract(("billing/asyncapi.yaml", """
            asyncapi: 3.0.0
            info:
              title: Billing events
              version: 2.1.0
            servers:
              main:
                host: kafka.acme.example:9092
                pathname: /prod
                protocol: kafka
              spare:
                host: kafka-2.acme.example:9092
                protocol: kafka-secure
            channels:
              invoices:
                address: billing.invoices.v1
                messages:
                  issued:
                    $ref: '#/components/messages/InvoiceIssued'
                  voided:
                    $ref: '#/components/messages/Nowhere'
            operations:
              publishInvoice:
                action: send
                channel:
                  $ref: '#/channels/invoices'
                messages:
                  - $ref: '#/channels/invoices/messages/issued'
                bindings:
                  kafka:
                    groupId:
                      type: string
                      enum: [billing-writers]
                    clientId:
                      const: hunter2-client
            components:
              messages:
                InvoiceIssued:
                  name: invoice.issued
                  title: Invoice issued
                  contentType: application/json
                  payload:
                    $ref: '#/components/schemas/Invoice'
              schemas:
                Invoice:
                  type: object
                  required: [id]
                  properties:
                    id: {type: string, format: uuid, example: hunter2-x}
            """));

        var document = result.Fact("contract:billing/asyncapi.yaml");
        Assert.Equal(("Billing events", "3.0.0", "2.1.0"), (document.Name, document.Attr("specVersion"), document.Attr("apiVersion")));
        Assert.Equal(("main,spare", "kafka,kafka-secure", "kafka.acme.example:9092/prod,kafka-2.acme.example:9092"),
            (document.Attr("servers"), document.Attr("protocols"), document.Attr("serverUrls")));
        Assert.Equal(["billing/asyncapi.yaml:1-1"], document.Lines());

        var channel = result.Fact("channel:billing/asyncapi.yaml#invoices");
        Assert.Equal(("billing.invoices.v1", "invoices"), (channel.Attr("address"), channel.Attr("channelId")));
        // The reference that does not resolve names no message.
        Assert.Equal("message:billing/asyncapi.yaml#InvoiceIssued", channel.Attr("messages"));
        Assert.Equal(["billing/asyncapi.yaml:14-20"], channel.Lines());

        var publication = result.Fact("publication:billing/asyncapi.yaml:publishInvoice");
        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal(("send", "channel:billing/asyncapi.yaml#invoices"), (publication.Attr("action"), publication.Attr("channel")));
        // The operation refers to the channel entry, which refers to the component: two hops, one message.
        Assert.Equal("message:billing/asyncapi.yaml#InvoiceIssued", publication.Attr("messages"));
        Assert.Equal("billing-writers", publication.Attr("kafka.groupId"));
        Assert.Equal(["billing/asyncapi.yaml:22-34"], publication.Lines());

        var message = result.Fact("message:billing/asyncapi.yaml#InvoiceIssued");
        Assert.Equal("invoice.issued", message.Name);
        Assert.Equal(("invoice.issued", "Invoice issued", "application/json"), (message.Attr("name"), message.Attr("title"), message.Attr("contentType")));
        Assert.Equal("schema:billing/asyncapi.yaml#/components/schemas/Invoice", message.Attr("payloadSchema"));
        Assert.Null(message.Attr("payload"));
        Assert.Equal(["billing/asyncapi.yaml:37-42"], message.Lines());

        var schema = result.Fact("schema:billing/asyncapi.yaml#/components/schemas/Invoice");
        Assert.Equal("""{"properties":{"id":{"format":"uuid","type":"string"}},"required":["id"],"type":"object"}""", schema.Attr("schema"));
        Assert.Equal(["billing/asyncapi.yaml:44-48"], schema.Lines());

        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "unresolved-message", "billing/asyncapi.yaml", 19)], result.Notes);
        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
        Assert.Equal(5, result.Facts.Count);
    }

    [Fact]
    public void A_binding_gives_identifier_values_of_known_fields_and_nothing_else()
    {
        var result = Repo.Extract(("q.yaml", """
            asyncapi: 2.6.0
            channels:
              jobs:
                bindings:
                  amqp:
                    is: routingKey
                    exchange:
                      name: jobs.exchange
                      type: topic
                      x-password: hunter2-a
                publish:
                  operationId: onJob
                  bindings:
                    kafka:
                      groupId:
                        enum: ["hunter2 b c"]
                    amqp:
                      cc: [jobs.created, "pass word hunter2-d"]
                  message:
                    messageId: job-created
                    payload:
                      type: string
                      const: hunter2-e
            """));

        var channel = result.Fact("channel:q.yaml#jobs");
        Assert.Equal(("amqp", "routingKey", "exchange", "jobs.exchange", "topic"),
            (channel.Attr("transport"), channel.Attr("amqp.is"), channel.Attr("kind"), channel.Attr("amqp.exchange"), channel.Attr("amqp.exchange.type")));

        var subscription = result.Fact("subscription:q.yaml:onJob");
        Assert.Null(subscription.Attr("kafka.groupId"));
        Assert.Equal("jobs.created", subscription.Attr("amqp.routingKeys"));
        Assert.Equal("""{"type":"string"}""", result.Fact("message:q.yaml#jobs/job-created").Attr("payload"));

        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Redacted, "binding-value-not-emitted", "q.yaml", 15),
                new ExtractionNote(NoteKind.Redacted, "binding-value-not-emitted", "q.yaml", 18),
            ],
            result.Notes);
    }

    // ---- the YAML and JSON reader ------------------------------------------------------

    [Fact]
    public void Leading_empty_documents_do_not_hide_a_contract()
    {
        var result = Repo.Extract(
            ("api.yaml", "---\n---\nopenapi: 3.0.0\npaths:\n  /x:\n    get:\n      responses: {}\n"),
            ("events.yaml", "---\n# nothing here\n---\nasyncapi: 2.6.0\nchannels:\n  a: {}\n"),
            ("compose.yaml", "---\n---\nservices:\n  db:\n    image: postgres:16\n"),
            (".github/workflows/ci.yml", "---\n---\non: push\njobs:\n  a:\n    steps:\n      - run: go build ./...\n"));

        Assert.Equal(["api.yaml:6-7"], result.Fact("operation:api.yaml:GET /x").Lines());
        Assert.Equal(["api.yaml:3-3"], result.Fact("contract:api.yaml").Lines());
        Assert.Equal(["events.yaml:6-6"], result.Fact("channel:events.yaml#a").Lines());
        Assert.Equal(["compose.yaml:4-5"], result.Fact("datastore:.:db").Lines());
        Assert.Equal("a", result.Fact("deployment:workflow:.github/workflows/ci.yml").Attr("buildJobs"));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Two_contract_documents_in_one_file_are_not_merged_into_one_contract()
    {
        var result = Repo.Extract(("two.yaml", "openapi: 3.0.0\npaths:\n  /a:\n    get: {}\n---\nopenapi: 3.0.0\npaths:\n  /b:\n    get: {}\n"));

        Assert.Empty(result.Facts);
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "multiple-documents", "two.yaml", 6)], result.Notes);
    }

    [Fact]
    public void A_whitespace_only_line_with_a_tab_does_not_fail_the_file()
    {
        var result = Repo.Extract(("api.yaml", "openapi: 3.0.0\n\t\npaths:\n  /x:\n    get:\n      responses: {}\n \t \n    put:\n      responses: {}\n"));

        Assert.Empty(result.Notes);
        Assert.Equal(["api.yaml:5-6"], result.Fact("operation:api.yaml:GET /x").Lines());
        Assert.Equal(["api.yaml:8-9"], result.Fact("operation:api.yaml:PUT /x").Lines());
    }

    [Fact]
    public void A_yaml_syntax_error_is_a_note_at_the_line_of_the_error()
    {
        var result = Repo.Extract(
            ("flow.yaml", "a: 1\nb: [1, 2\nc: 3\n"),
            ("end.yaml", "a: 1\nb: 2\n\n\n\nc: [\n"),
            ("mapping.yaml", "a: 1\nb: 2\n c: 3\nd: 4\n"));

        Assert.Empty(result.Facts);
        Assert.Equal(
            [
                // The file has six lines: the note is not at a seventh.
                new ExtractionNote(NoteKind.ParseFailure, "yaml-syntax", "end.yaml", 6),
                // The sequence that is not closed starts in line 2.
                new ExtractionNote(NoteKind.ParseFailure, "yaml-syntax", "flow.yaml", 2),
                new ExtractionNote(NoteKind.ParseFailure, "yaml-syntax", "mapping.yaml", 3),
            ],
            result.Notes);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_files()
    {
        (string, string)[] files =
        [
            ("api/openapi.json", OpenApiJson), ("openapi.yaml", OpenApiYaml), ("asyncapi.yaml", AsyncApiV2),
            ("b/asyncapi.yaml", AsyncApiV2), ("a/openapi.yaml", OpenApiYaml),
        ];

        var forward = ExtractionJson.Serialize(Repo.Extract(files));
        var backward = ExtractionJson.Serialize(Repo.Extract([.. files.Reverse()]));

        Assert.Equal(forward, backward);
    }

    // ---- bounded work ------------------------------------------------------------------

    [Fact]
    public void Twenty_thousand_paths_are_read_in_bounded_work()
    {
        var content = HostileContracts.OpenApiPaths();
        Assert.True(content.Split("\"/").Length > 20_000);

        var (result, work) = Repo.ExtractBounded("openapi.json", content);

        Assert.True(work > 0);
        Assert.Equal("same", result.Fact("operation:openapi.json:GET /o0").Attr("operationId"));
        Assert.Contains("duplicate-operation-id", result.Reasons());
        // 2 000 operations and the document are more facts than one file may give.
        Assert.Contains("limit:facts-per-file", result.Reasons());
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Thousands_of_schema_references_are_checked_in_bounded_work()
    {
        var (result, work) = Repo.ExtractBounded("openapi.json", HostileContracts.OpenApiReferences());

        Assert.True(work > 0);
        Assert.Equal("schema:openapi.json#/components/schemas/s7", result.Fact("operation:openapi.json:POST /o7").Attr("requestSchema"));
        Assert.Equal("""{"$ref":"#/components/schemas/s6"}""", result.Fact("schema:openapi.json#/components/schemas/s7").Attr("schema"));
    }

    [Fact]
    public void A_long_chain_of_path_item_references_stops_at_the_documented_number_of_hops()
    {
        var (result, work) = Repo.ExtractBounded("openapi.json", HostileContracts.OpenApiPathItemChain());

        Assert.True(work > 0);
        // Facts are in kind order: Operation, then Schema.
        Assert.Equal(["operation:openapi.json:GET /end", "contract:openapi.json"], result.Facts.Select(f => f.Key));
        Assert.Contains("ref-too-deep", result.Reasons());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_wide_schema_gives_no_body_and_the_writer_stops_at_the_bound(bool properties)
    {
        var content = properties ? HostileContracts.OpenApiWideSchema() : HostileContracts.OpenApiWideEnum();

        var (result, work) = Repo.ExtractBounded("openapi.json", content);

        Assert.True(work > 0);
        Assert.Null(result.Fact("schema:openapi.json#/components/schemas/Wide").Attr("schema"));
        Assert.Contains(new ExtractionNote(NoteKind.LimitReached, "limit:schema-bytes", "openapi.json", 1), result.Notes);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void A_schema_used_through_many_aliases_does_not_multiply_the_output()
    {
        var content = HostileContracts.OpenApiAliases();

        // The kit holds the output under 4 MiB. Each of the 99 uses alone is under the schema bound.
        var (result, _) = Repo.ExtractBounded("openapi.yaml", content);

        Assert.NotNull(result.Fact("schema:openapi.yaml#/components/schemas/S0").Attr("schema"));
        Assert.Null(result.Fact("schema:openapi.yaml#/components/schemas/S98").Attr("schema"));
        Assert.Contains("limit:schema-bytes-per-file", result.Reasons());
        Assert.False(result.IsComplete);
        Assert.True(Encoding.UTF8.GetByteCount(ExtractionJson.Serialize(result)) < 2 * Encoding.UTF8.GetByteCount(content) + 512 * 1024);
    }

    [Fact]
    public void Thousands_of_message_references_are_resolved_in_bounded_work()
    {
        var (result, work) = Repo.ExtractBounded("asyncapi.json", HostileContracts.AsyncApiReferences());

        Assert.True(work > 0);
        // c3 refers to m3, which refers down to m0: four hops.
        Assert.Equal("message:asyncapi.json#m0", result.Fact("channel:asyncapi.json#c3").Attr("messages"));
        // c100 would need a hundred hops.
        Assert.Null(result.Fact("channel:asyncapi.json#c100").Attr("messages"));
        Assert.Contains("unresolved-message", result.Reasons());
    }

    [Fact]
    public void Thousands_of_channels_are_read_in_bounded_work()
    {
        var (result, work) = Repo.ExtractBounded("asyncapi.yaml", HostileContracts.AsyncApiChannels());

        Assert.True(work > 0);
        Assert.Equal("message:asyncapi.yaml#c0/M0", result.Fact("channel:asyncapi.yaml#c0").Attr("messages"));
        Assert.Contains("limit:facts-per-file", result.Reasons());
    }
}

/// <summary>Inputs just under the 256 KiB file bound, each built to make one loop of an extractor expensive.</summary>
internal static class HostileText
{
    public const int Bound = 262_000;

    /// <summary>The head, then as many units as fit, then the tail.</summary>
    public static string Fill(string head, Func<int, string> unit, string tail = "", int bound = Bound)
    {
        var builder = new StringBuilder(head, bound + 256);
        for (var i = 0; ; i++)
        {
            var next = unit(i);
            if (builder.Length + next.Length + tail.Length > bound)
                break;
            builder.Append(next);
        }
        return builder.Append(tail).ToString();
    }
}

internal static class HostileContracts
{
    /// <summary>More than 20 000 keys in <c>paths</c>: most are empty path items, 2 000 have an operation, 100 of them with one shared id.</summary>
    public static string OpenApiPaths()
    {
        var withOperations = string.Concat(Enumerable.Range(0, 2000).Select(i =>
            i < 100 ? $"\"/o{i}\":{{\"get\":{{\"operationId\":\"same\"}}}}," : $"\"/o{i}\":{{\"get\":{{}}}},"));
        return HostileText.Fill("{\"openapi\":\"3.0.0\",\"paths\":{" + withOperations, i => $"\"/{i}\":{{}},", "\"/end\":{}}}");
    }

    /// <summary>2 500 schemas that each refer to the one before, and operations that each refer to one of them.</summary>
    public static string OpenApiReferences()
    {
        var schemas = string.Join(",", Enumerable.Range(0, 2500).Select(i =>
            i == 0 ? "\"s0\":{\"type\":\"object\"}" : $"\"s{i}\":{{\"$ref\":\"#/components/schemas/s{i - 1}\"}}"));
        return HostileText.Fill(
            "{\"openapi\":\"3.0.0\",\"components\":{\"schemas\":{" + schemas + "}},\"paths\":{",
            i => $"\"/o{i}\":{{\"post\":{{\"requestBody\":{{\"content\":{{\"application/json\":{{\"schema\":{{\"$ref\":\"#/components/schemas/s{i % 2500}\"}}}}}}}},\"responses\":{{}}}}}},",
            "\"/end\":{}}}");
    }

    /// <summary>Path items that each refer to the next one, in one chain of thousands.</summary>
    public static string OpenApiPathItemChain() =>
        HostileText.Fill("{\"openapi\":\"3.0.0\",\"paths\":{", i => $"\"/p{i}\":{{\"$ref\":\"#/paths/~1p{i + 1}\"}},", "\"/end\":{\"get\":{}}}}");

    /// <summary>One schema with tens of thousands of properties.</summary>
    public static string OpenApiWideSchema() =>
        HostileText.Fill("{\"openapi\":\"3.0.0\",\"paths\":{},\"components\":{\"schemas\":{\"Wide\":{\"properties\":{", i => $"\"p{i}\":{{}},", "\"z\":{}}}}}}");

    /// <summary>One enum with tens of thousands of members.</summary>
    public static string OpenApiWideEnum() =>
        HostileText.Fill("{\"openapi\":\"3.0.0\",\"paths\":{},\"components\":{\"schemas\":{\"Wide\":{\"enum\":[", i => $"\"m{i}\",", "\"z\"]}}}}");

    /// <summary>
    /// A schema of about 58 KiB used through an alias 99 times, which is what the alias budget
    /// of the reader allows. The file is 60 KiB; the bodies together would be 5.7 MiB.
    /// </summary>
    public static string OpenApiAliases()
    {
        var text = new string('d', 1200);
        var builder = new StringBuilder("openapi: 3.0.0\npaths: {}\nx-shared: &big\n  type: object\n  properties:\n");
        for (var i = 0; i < 48; i++)
            builder.Append($"    p{i}: {{description: {text}}}\n");
        builder.Append("components:\n  schemas:\n");
        for (var i = 0; i < 99; i++)
            builder.Append($"    S{i}: *big\n");
        return builder.ToString();
    }

    /// <summary>2 000 messages that each refer to the one before, and channels that each refer to one of them.</summary>
    public static string AsyncApiReferences()
    {
        var messages = string.Join(",", Enumerable.Range(0, 2000).Select(i =>
            i == 0 ? "\"m0\":{\"payload\":{\"type\":\"object\"}}" : $"\"m{i}\":{{\"$ref\":\"#/components/messages/m{i - 1}\"}}"));
        return HostileText.Fill(
            "{\"asyncapi\":\"3.0.0\",\"components\":{\"messages\":{" + messages + "}},\"channels\":{",
            i => $"\"c{i}\":{{\"messages\":{{\"m\":{{\"$ref\":\"#/components/messages/m{i % 2000}\"}}}}}},",
            "\"end\":{}}}");
    }

    public static string AsyncApiChannels() =>
        HostileText.Fill("asyncapi: 2.6.0\nchannels:\n", i => $"  c{i}: {{publish: {{message: {{name: M{i}, payload: {{type: object}}}}}}}}\n");

    public static IEnumerable<(string Name, string Path, string Content)> All() =>
    [
        ("openapi-paths", "openapi.json", OpenApiPaths()),
        ("openapi-references", "openapi.json", OpenApiReferences()),
        ("openapi-path-item-chain", "openapi.json", OpenApiPathItemChain()),
        ("openapi-wide-schema", "openapi.json", OpenApiWideSchema()),
        ("openapi-wide-enum", "openapi.json", OpenApiWideEnum()),
        ("openapi-aliases", "openapi.yaml", OpenApiAliases()),
        ("asyncapi-references", "asyncapi.json", AsyncApiReferences()),
        ("asyncapi-channels", "asyncapi.yaml", AsyncApiChannels()),
    ];
}
