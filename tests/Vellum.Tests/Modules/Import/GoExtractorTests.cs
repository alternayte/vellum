using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>The Go rules on small sources: what each recognises, what it refuses, and the exact lines.</summary>
public class GoExtractorTests
{
    private const string GoMod = """
        module github.com/acme/shop/stock/v2

        go 1.22

        require github.com/go-chi/chi/v5 v5.1.0
        require (
        	github.com/segmentio/kafka-go v0.4.47
        	github.com/stretchr/testify v1.9.0 // indirect
        )
        """;

    private static ExtractionResult Stock(string source, string name = "main.go") =>
        Repo.Extract(("stock/go.mod", GoMod), ($"stock/{name}", source));

    private static string[] Operations(ExtractionResult result) =>
        result.Facts.Where(f => f.Kind == FactKind.Operation).Select(f => f.Key).ToArray();

    [Fact]
    public void A_module_is_a_service_named_by_its_path_and_its_requirements_are_dependencies()
    {
        var result = Stock("package main\n\nfunc main() {}\n");

        var service = result.Fact("service:stock");
        Assert.Equal(("go", "github.com/acme/shop/stock/v2", "1.22", "true"),
            (service.Attr("language"), service.Attr("module"), service.Attr("goVersion"), service.Attr("hasMain")));
        Assert.Equal(["stock/go.mod:1-1"], service.Lines());

        var chi = result.Fact("dependency:stock:go:github.com/go-chi/chi/v5");
        Assert.Equal(("chi", "http-router", "v5.1.0"), (chi.Attr("technology"), chi.Attr("role"), chi.Attr("version")));
        Assert.Equal(["stock/go.mod:5-5"], chi.Lines());
        Assert.Equal("kafka", result.Fact("dependency:stock:go:github.com/segmentio/kafka-go").Attr("technology"));
        var testify = result.Fact("dependency:stock:go:github.com/stretchr/testify");
        Assert.Equal("true", testify.Attr("indirect"));
        Assert.Null(testify.Attr("technology"));
        Assert.Equal(["stock/go.mod:8-8"], testify.Lines());
    }

    [Fact]
    public void A_library_module_has_no_main_and_test_files_and_files_without_a_module_are_not_read()
    {
        var result = Repo.Extract(
            ("lib/go.mod", "module github.com/acme/lib\n"),
            ("lib/lib.go", "package lib\n\nimport \"net/http\"\n\nfunc Register() {\n\thttp.HandleFunc(\"/lib\", nil)\n}\n"),
            ("lib/lib_test.go", "package lib\n\nimport \"net/http\"\n\nfunc init() {\n\thttp.HandleFunc(\"/only-in-test\", nil)\n}\n"),
            ("loose/main.go", "package main\n\nimport \"net/http\"\n\nfunc main() {\n\thttp.HandleFunc(\"/loose\", nil)\n}\n"));

        Assert.Equal("false", result.Fact("service:lib").Attr("hasMain"));
        Assert.Equal(["operation:lib:ANY /lib"], Operations(result));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "no-owning-module", "loose/main.go", 0)], result.Notes);
    }

    [Fact]
    public void Net_http_patterns_give_method_host_and_path()
    {
        var result = Stock("""
            package main

            import (
            	"fmt"
            	"net/http"
            )

            const itemsPattern = "GET /items/{sku}"

            func main() {
            	mux := http.NewServeMux()
            	mux.HandleFunc(itemsPattern, getItem)
            	mux.Handle("POST api.acme.example/items/{rest...}", http.HandlerFunc(postItem))
            	mux.HandleFunc("/static/", static)
            	mux.HandleFunc("GET /{$}", home)
            	http.HandleFunc("/default-mux", other)
            	// mux.HandleFunc("GET /commented", nil)
            	fmt.Println(`mux.HandleFunc("GET /raw-string", nil)`, "mux.HandleFunc(\"GET /string\", nil)")
            	pattern := "GET /variable"
            	mux.HandleFunc(pattern, nil)
            	mux.HandleFunc("GET "+prefix()+"/built", nil)
            	http.ListenAndServe(":8080", mux)
            }
            """);

        Assert.Equal(
            [
                "operation:stock:ANY /default-mux",
                "operation:stock:ANY /static/",
                "operation:stock:GET /",
                "operation:stock:GET /items/{sku}",
                "operation:stock:POST /items/{rest}",
            ],
            Operations(result));

        var get = result.Fact("operation:stock:GET /items/{sku}");
        Assert.Equal(("net/http", "GET", "/items/{sku}", "service:stock"), (get.Attr("framework"), get.Attr("method"), get.Attr("path"), get.Attr("service")));
        Assert.Equal(["stock/main.go:12-12"], get.Lines());
        var post = result.Fact("operation:stock:POST /items/{rest}");
        Assert.Equal("api.acme.example", post.Attr("host"));
        Assert.Equal("POST api.acme.example/items/{rest...}", post.Attr("routeTemplate"));

        Assert.Equal([(20, "dynamic-route"), (21, "dynamic-route")], result.Notes.Select(n => (n.Line, n.Reason)));
    }

    [Fact]
    public void Chi_routes_take_the_prefix_of_route_blocks_and_of_mounts_in_the_same_file()
    {
        var result = Stock("""
            package main

            import (
            	"net/http"

            	"github.com/go-chi/chi/v5"
            	"github.com/go-chi/chi/v5/middleware"
            )

            func main() {
            	r := chi.NewRouter()
            	r.Use(middleware.Logger)
            	r.Get("/healthz", health)
            	r.Route("/api", func(r chi.Router) {
            		r.Post("/items", create)
            		r.Route("/items/{sku}", func(r chi.Router) {
            			r.Get("/", get)
            			r.With(auth).Delete("/", remove)
            		})
            		r.Group(func(r chi.Router) {
            			r.Method("PUT", "/bulk", bulk)
            		})
            	})
            	admin := chi.NewRouter()
            	admin.Get("/stats", stats)
            	r.Mount("/admin", admin)
            	r.Get("/after", after)
            	http.ListenAndServe(":8080", r)
            }

            func reports() chi.Router {
            	sub := chi.NewRouter()
            	sub.Get("/daily", daily)
            	return sub
            }

            func register(r chi.Router) {
            	r.Get("/registered", nil)
            }
            """);

        Assert.Equal(
            [
                "operation:stock:GET /admin/stats",
                "operation:stock:GET /after",
                "operation:stock:GET /api/items/{sku}",
                "operation:stock:GET /healthz",
                "operation:stock:POST /api/items",
                "operation:stock:PUT /api/bulk",
            ],
            Operations(result));
        Assert.Equal(["stock/main.go:17-17"], result.Fact("operation:stock:GET /api/items/{sku}").Lines());
        Assert.Equal("chi", result.Fact("operation:stock:GET /admin/stats").Attr("framework"));

        // "r.With(auth).Delete" has a call as its receiver; a returned router and a router
        // parameter have a mount point that this file does not fix.
        Assert.Equal(
            [(33, "unresolved-route-prefix"), (38, "unresolved-route-prefix")],
            result.Notes.Select(n => (n.Line, n.Reason)));
    }

    [Fact]
    public void Gin_and_echo_routes_take_group_prefixes_and_colon_parameters()
    {
        var gin = Stock("""
            package main

            import "github.com/gin-gonic/gin"

            func main() {
            	r := gin.Default()
            	r.GET("/ping", ping)
            	v1 := r.Group("/v1")
            	{
            		v1.POST("/items/:sku", create)
            		files := v1.Group("/files")
            		files.GET("/*path", serve)
            	}
            	r.Any("/any", any)
            	r.Handle("PATCH", "/items/:sku", patch)
            }

            func extra(g *gin.RouterGroup, e *gin.Engine) {
            	g.GET("/in-unknown-group", nil)
            	e.GET("/on-engine", nil)
            }
            """);

        Assert.Equal(
            [
                "operation:stock:ANY /any", "operation:stock:GET /on-engine", "operation:stock:GET /ping",
                "operation:stock:GET /v1/files/{path}", "operation:stock:PATCH /items/{sku}", "operation:stock:POST /v1/items/{sku}",
            ],
            Operations(gin));
        Assert.Equal("/v1/items/:sku", gin.Fact("operation:stock:POST /v1/items/{sku}").Attr("routeTemplate"));
        Assert.Equal([(19, "unresolved-route-prefix")], gin.Notes.Select(n => (n.Line, n.Reason)));

        var echo = Stock("""
            package main

            import "github.com/labstack/echo/v4"

            func main() {
            	e := echo.New()
            	api := e.Group("/api")
            	api.GET("/users/:id", getUser)
            	e.Add("DELETE", "/users/:id", deleteUser)
            }
            """);

        Assert.Equal(["operation:stock:DELETE /users/{id}", "operation:stock:GET /api/users/{id}"], Operations(echo));
        Assert.Equal("echo", echo.Fact("operation:stock:GET /api/users/{id}").Attr("framework"));
    }

    [Fact]
    public void Amqp_publish_kafka_topics_and_nats_subjects_are_channels_with_publications_and_subscriptions()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/amqp.go", """
                package main

                import amqp "github.com/rabbitmq/amqp091-go"

                func publish(ctx context.Context, ch *amqp.Channel, key string) {
                	ch.PublishWithContext(ctx, "stock-events", "stock.reserved", false, false, amqp.Publishing{})
                	ch.PublishWithContext(ctx, "", "audit-queue", false, false, amqp.Publishing{})
                	ch.PublishWithContext(ctx, "stock-events", key, false, false, amqp.Publishing{})
                	ch.QueueDeclare("", false, true, true, false, nil)
                }
                """),
            ("stock/kafka.go", """
                package main

                import "github.com/segmentio/kafka-go"

                const reservedTopic = "stock.reserved.v1"

                func streams(name string) {
                	reader := kafka.NewReader(kafka.ReaderConfig{
                		Brokers: []string{"kafka:9092"},
                		Topic:   reservedTopic,
                		GroupID: "stock",
                	})
                	writer := &kafka.Writer{Addr: kafka.TCP("kafka:9092"), Topic: "stock.released.v1"}
                	_ = kafka.Message{Topic: name}
                }
                """),
            ("stock/nats.go", """
                package main

                import "github.com/nats-io/nats.go"

                func subjects(nc *nats.Conn, subject string) {
                	nc.Subscribe("stock.>", handle)
                	nc.QueueSubscribe("stock.audit", "auditors", handle)
                	nc.Publish("stock.low", nil)
                	nc.Publish(subject, nil)
                }
                """));

        var publication = result.Fact("publication:stock:amqp:stock-events:stock.reserved");
        Assert.Equal(("amqp", "channel:stock-events", "stock.reserved"), (publication.Attr("transport"), publication.Attr("channel"), publication.Attr("routingKey")));
        Assert.Equal(["stock/amqp.go:6-6"], publication.Lines());
        // The default exchange routes to the queue that the routing key names.
        Assert.Equal("channel:audit-queue", result.Fact("publication:stock:amqp::audit-queue").Attr("channel"));

        Assert.Equal(("topic", "kafka"), (result.Fact("channel:stock.reserved.v1").Attr("kind"), result.Fact("channel:stock.reserved.v1").Attr("transport")));
        Assert.Equal(["stock/kafka.go:10-10"], result.Fact("subscription:stock:kafka:stock.reserved.v1").Lines());
        Assert.Equal("channel:stock.released.v1", result.Fact("publication:stock:kafka:stock.released.v1").Attr("channel"));

        Assert.Equal("subject", result.Fact("channel:stock.>").Attr("kind"));
        Assert.True(result.Has("subscription:stock:nats:stock.>"));
        Assert.True(result.Has("subscription:stock:nats:stock.audit"));
        Assert.Equal(["stock/nats.go:8-8"], result.Fact("publication:stock:nats:stock.low").Lines());

        Assert.Equal(
            [
                ("stock/amqp.go", 8, "dynamic-channel-name"), ("stock/amqp.go", 9, "dynamic-channel-name"),
                ("stock/kafka.go", 14, "dynamic-channel-name"), ("stock/nats.go", 9, "dynamic-channel-name"),
            ],
            result.Notes.Select(n => (n.Path, n.Line, n.Reason)));
    }

    [Fact]
    public void A_binding_with_a_routing_key_and_confluent_topics_are_read()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/bind.go", """
                package main

                import amqp "github.com/rabbitmq/amqp091-go"

                func bind(ctx context.Context, ch *amqp.Channel) {
                	ch.QueueBind("stock.audit", "stock.#", "stock-events", false, nil)
                	ch.ConsumeWithContext(ctx, "stock.audit", "", false, false, false, false, nil)
                }
                """),
            ("stock/confluent.go", """
                package main

                import "github.com/confluentinc/confluent-kafka-go/v2/kafka"

                const auditTopic = "stock.audit.v1"

                func consume(c *kafka.Consumer, p *kafka.Producer, topic string) {
                	c.SubscribeTopics([]string{"stock.reserved.v1", auditTopic}, nil)
                	p.Produce(&kafka.Message{TopicPartition: kafka.TopicPartition{Topic: &topic, Partition: kafka.PartitionAny}}, nil)
                }
                """));

        var binding = result.Fact("binding:stock.audit:stock-events:stock.#");
        Assert.Equal(("channel:stock.audit", "channel:stock-events", "stock.#"), (binding.Attr("queue"), binding.Attr("exchange"), binding.Attr("routingKey")));
        Assert.Equal(["stock/bind.go:6-6"], binding.Lines());
        Assert.Equal(["stock/bind.go:7-7"], result.Fact("subscription:stock:amqp:stock.audit").Lines());

        Assert.Equal(["stock/confluent.go:8-8"], result.Fact("subscription:stock:kafka:stock.reserved.v1").Lines());
        Assert.True(result.Has("subscription:stock:kafka:stock.audit.v1"));
        // A topic passed as a pointer to a variable is not known from the text.
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-channel-name", "stock/confluent.go", 9)], result.Notes);
        Assert.DoesNotContain(result.Facts, f => f.Kind == FactKind.Publication);
    }

    [Fact]
    public void Broker_calls_are_ignored_in_a_file_that_does_not_import_the_broker_package()
    {
        var result = Stock("""
            package main

            func run(ch Channel, nc Bus) {
            	ch.ExchangeDeclare("looks-like-amqp", "topic", true, false, false, false, nil)
            	ch.Consume("looks-like-amqp", "", false, false, false, false, nil)
            	nc.Publish("looks-like-nats", nil)
            }
            """);

        Assert.DoesNotContain(result.Facts, f => f.Kind is FactKind.Channel or FactKind.Publication or FactKind.Subscription);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Database_access_records_the_driver_and_never_the_connection_string()
    {
        var result = Stock("""
            package main

            import (
            	"database/sql"

            	"github.com/jackc/pgx/v5/pgxpool"
            	"github.com/jmoiron/sqlx"
            )

            func open(driver string) {
            	a, _ := sql.Open("mysql", "app:pw@tcp(db:3306)/shop")
            	b, _ := sqlx.Connect("sqlite3", "file:shop.db")
            	c, _ := pgxpool.New(ctx, "postgres://app:pw@db/shop")
            	d, _ := sql.Open(driver, dsn)
            	e, _ := sql.Open("odbc", dsn)
            }
            """);

        var mysql = result.Fact("dataaccess:stock:sql:mysql");
        Assert.Equal(FactKind.DataAccess, mysql.Kind);
        Assert.Equal(("mysql", "mysql", "service:stock"), (mysql.Attr("driver"), mysql.Attr("storeKind"), mysql.Attr("service")));
        Assert.Equal(["stock/main.go:11-11"], mysql.Lines());
        Assert.Equal("sqlite", result.Fact("dataaccess:stock:sql:sqlite3").Attr("storeKind"));
        Assert.Equal("postgres", result.Fact("dataaccess:stock:pgx").Attr("storeKind"));
        Assert.Equal("unknown", result.Fact("dataaccess:stock:sql:odbc").Attr("storeKind"));
        Assert.Equal([(14, "dynamic-driver-name")], result.Notes.Select(n => (n.Line, n.Reason)));
        Assert.DoesNotContain("app:pw", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void A_constant_with_two_values_in_one_package_is_not_resolved()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/a.go", "package main\n\nimport \"net/http\"\n\nconst pattern = \"GET /a\"\n\nfunc a() {\n\thttp.HandleFunc(pattern, nil)\n}\n"),
            ("stock/b.go", "package main\n\nfunc b() {\n\tconst pattern = \"GET /b\"\n}\n"));

        Assert.Empty(Operations(result));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-route", "stock/a.go", 8)], result.Notes);
    }
}
