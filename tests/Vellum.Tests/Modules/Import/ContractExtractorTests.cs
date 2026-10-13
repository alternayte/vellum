using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>OpenAPI and AsyncAPI, in the forms the Orders fixture does not use.</summary>
public class ContractExtractorTests
{
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
        Assert.Equal("putItem", put.Attr("operationId"));
        Assert.Equal("true", put.Attr("security"));
        Assert.Equal("application/json", put.Attr("requestContentType"));
        Assert.Equal("schema:api/openapi.json#/components/schemas/Item", put.Attr("requestSchema"));
        Assert.Equal("204,default", put.Attr("responses"));
        Assert.Equal("external-ref", put.Attr("response.default.schema"));
        Assert.Equal("contract:api/openapi.json", put.Attr("contract"));
        Assert.Equal(["api/openapi.json:9-22"], put.Lines());

        var delete = result.Fact("operation:api/openapi.json:DELETE /items/{sku}");
        Assert.Equal("true", delete.Attr("deprecated"));
        Assert.Equal("false", delete.Attr("security"));
        Assert.Equal(["api/openapi.json:23-23"], delete.Lines());

        var schema = result.Fact("schema:api/openapi.json#/components/schemas/Item");
        Assert.Equal(FactKind.Schema, schema.Kind);
        // Keys sorted; the example and the extension field are left out.
        Assert.Equal(
            """{"properties":{"count":{"type":"integer"},"sku":{"type":"string"}},"type":"object"}""",
            schema.Attr("schema"));
        Assert.Equal(["api/openapi.json:29-33"], schema.Lines());

        var document = result.Fact("contract:api/openapi.json");
        Assert.Equal("3.1.0", document.Attr("specVersion"));
        Assert.Equal("https://stock.acme.example/api", document.Attr("servers"));
        Assert.Equal(["api/openapi.json:2-2", "api/openapi.json:4-6"], document.Lines());

        // A path item behind $ref is not followed and not guessed.
        Assert.DoesNotContain(result.Facts, f => f.Key.Contains("/shared"));
        var note = Assert.Single(result.Notes);
        Assert.Equal(new ExtractionNote(NoteKind.Undecided, "external-ref", "api/openapi.json", 25), note);
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
        """;

    [Fact]
    public void AsyncApi_2_reads_the_operation_words_from_the_side_of_the_application()
    {
        var result = Repo.Extract(("asyncapi.yaml", AsyncApiV2));

        var channel = result.Fact("channel:stock.reserved");
        Assert.Equal("kafka", channel.Attr("transport"));
        Assert.Equal("topic", channel.Attr("kind"));
        Assert.Equal("stock.reserved.v1", channel.Attr("kafka.topic"));
        Assert.Equal("message:StockReleased,message:StockReserved", channel.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:10-29"], channel.Lines());

        // "subscribe" in 2.x: others subscribe, so the application publishes.
        var publication = result.Fact("publication:asyncapi.yaml:emitStockReserved");
        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal("subscribe", publication.Attr("action"));
        Assert.Equal("channel:stock.reserved", publication.Attr("channel"));
        Assert.Equal("message:StockReserved", publication.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:14-17"], publication.Lines());

        // No operationId: the key is the channel and the word.
        var subscription = result.Fact("subscription:asyncapi.yaml:stock.reserved:publish");
        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal("auditors", subscription.Attr("kafka.groupId"));
        Assert.Equal("message:StockReleased,message:StockReserved", subscription.Attr("messages"));
        Assert.Equal(["asyncapi.yaml:18-29"], subscription.Lines());

        // The component message takes its name from its key; its payload leaves the file and is not fetched.
        var reserved = result.Fact("message:StockReserved");
        Assert.Equal("external-ref", reserved.Attr("payloadSchema"));
        Assert.Null(reserved.Attr("payload"));
        Assert.Equal(["asyncapi.yaml:32-35"], reserved.Lines());

        var released = result.Fact("message:StockReleased");
        Assert.Equal("inline", released.Attr("payloadSchema"));
        Assert.Equal("""{"properties":{"sku":{"type":"string"}},"type":"object"}""", released.Attr("payload"));
        Assert.Equal(["asyncapi.yaml:25-29"], released.Lines());

        var document = result.Fact("contract:asyncapi.yaml");
        Assert.Equal("production", document.Attr("servers"));
        Assert.Equal("kafka", document.Attr("protocols"));
        Assert.Empty(result.Notes);
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

        var channel = result.Fact("channel:audit.queue");
        Assert.Equal("queue", channel.Attr("kind"));
        Assert.Equal("audit.queue", channel.Attr("amqp.queue"));
        Assert.Equal("audit", channel.Attr("channelId"));
        Assert.Equal(["events/asyncapi.json:4-8"], channel.Lines());

        var subscription = result.Fact("subscription:events/asyncapi.json:onAudited");
        Assert.Equal("receive", subscription.Attr("action"));
        Assert.Equal("channel:audit.queue", subscription.Attr("channel"));
        // The operation lists no messages, so it carries the messages of its channel.
        Assert.Equal("message:Audited", subscription.Attr("messages"));
        Assert.Equal("audit.#,admin.*", subscription.Attr("amqp.routingKeys"));
        Assert.Equal(["events/asyncapi.json:11-15"], subscription.Lines());

        Assert.Equal(["events/asyncapi.json:6-6"], result.Fact("message:Audited").Lines());
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "unresolved-channel", "events/asyncapi.json", 16),
                new ExtractionNote(NoteKind.Undecided, "unknown-asyncapi-action", "events/asyncapi.json", 17),
            ],
            result.Notes);
    }
}
