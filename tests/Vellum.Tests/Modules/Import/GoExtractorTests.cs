using System.Text;
using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>
/// The Go rules on small sources: what each recognises, what it refuses, and the exact keys,
/// attributes and lines. Every expected value is written by hand from the source in the test.
/// </summary>
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

    private static string[] Keys(ExtractionResult result, params FactKind[] kinds) =>
        result.Facts.Where(f => kinds.Contains(f.Kind)).Select(f => f.Key).ToArray();

    private static (int Line, string Reason)[] Notes(ExtractionResult result) =>
        result.Notes.Select(n => (n.Line, n.Reason)).ToArray();

    // ---- go.module, go.require --------------------------------------------------------

    [Fact]
    public void A_module_is_a_service_keyed_by_its_directory_and_its_requirements_are_dependencies()
    {
        var result = Stock("package main\n\nfunc main() {}\n");

        var service = result.Fact("service:stock");
        Assert.Equal(FactKind.Service, service.Kind);
        Assert.Equal("stock", service.Name);
        Assert.Equal(("go", "github.com/acme/shop/stock/v2", "1.22", "true", "stock/go.mod"),
            (service.Attr("language"), service.Attr("module"), service.Attr("goVersion"), service.Attr("hasMain"), service.Attr("project")));
        Assert.Equal(["stock/go.mod:1-1"], service.Lines());
        Assert.Equal(("go.module", 2), (service.Evidence[0].Rule, service.Evidence[0].RuleVersion));

        var chi = result.Fact("dependency:stock:go:github.com/go-chi/chi/v5");
        Assert.Equal(FactKind.Dependency, chi.Kind);
        Assert.Equal(("chi", "http-router", "v5.1.0", "go", "service:stock"),
            (chi.Attr("technology"), chi.Attr("role"), chi.Attr("version"), chi.Attr("ecosystem"), chi.Attr("service")));
        Assert.Equal(["stock/go.mod:5-5"], chi.Lines());
        Assert.Equal("go.require", chi.Evidence[0].Rule);
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
    public void Two_modules_whose_paths_end_in_the_same_element_have_different_service_and_operation_keys()
    {
        const string source = "package main\n\nimport \"net/http\"\n\nfunc main() {\n\thttp.HandleFunc(\"GET /status\", nil)\n}\n";
        var result = Repo.Extract(
            ("billing/worker/go.mod", "module github.com/acme/billing/worker\n\nrequire github.com/lib/pq v1.10.9\n"),
            ("billing/worker/main.go", source),
            ("shipping/worker/go.mod", "module github.com/acme/shipping/worker\n\nrequire github.com/lib/pq v1.10.9\n"),
            ("shipping/worker/main.go", source));

        Assert.Equal(["service:billing/worker", "service:shipping/worker"], Keys(result, FactKind.Service));
        Assert.Equal(["worker", "worker"], result.Facts.Where(f => f.Kind == FactKind.Service).Select(f => f.Name));
        Assert.Equal(["operation:billing/worker:GET /status", "operation:shipping/worker:GET /status"], Operations(result));
        Assert.Equal(["billing/worker/main.go:6-6"], result.Fact("operation:billing/worker:GET /status").Lines());
        Assert.Equal("service:shipping/worker", result.Fact("operation:shipping/worker:GET /status").Attr("service"));
        Assert.Equal(
            ["dependency:billing/worker:go:github.com/lib/pq", "dependency:shipping/worker:go:github.com/lib/pq"],
            Keys(result, FactKind.Dependency));
    }

    // ---- go.net-http-route ------------------------------------------------------------

    [Fact]
    public void Net_http_patterns_give_method_host_path_and_the_meaning_of_a_trailing_slash()
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
                "operation:stock:ANY /static",
                "operation:stock:GET /",
                "operation:stock:GET /items/{sku}",
                "operation:stock:POST /items/{rest}",
            ],
            Operations(result));

        var get = result.Fact("operation:stock:GET /items/{sku}");
        Assert.Equal("GET /items/{sku}", get.Name);
        Assert.Equal(("net/http", "http", "GET", "/items/{sku}", "/items/{}", "GET /items/{sku}", "service:stock", "stock/go.mod"),
            (get.Attr("framework"), get.Attr("transport"), get.Attr("method"), get.Attr("path"), get.Attr("routeTemplate"),
                get.Attr("declaredRoute"), get.Attr("service"), get.Attr("project")));
        Assert.Equal((null, null, null), (get.Attr("host"), get.Attr("subtree"), get.Attr("exact")));
        Assert.Equal(["stock/main.go:12-12"], get.Lines());
        Assert.Equal(("go.net-http-route", 2), (get.Evidence[0].Rule, get.Evidence[0].RuleVersion));

        var post = result.Fact("operation:stock:POST /items/{rest}");
        Assert.Equal(("api.acme.example", "POST api.acme.example/items/{rest...}", "/items/{}"),
            (post.Attr("host"), post.Attr("declaredRoute"), post.Attr("routeTemplate")));

        // "/static/" matches every path below it; "/{$}" matches "/" and nothing else.
        var subtree = result.Fact("operation:stock:ANY /static");
        Assert.Equal(("/static", "/static/", "true", null), (subtree.Attr("path"), subtree.Attr("declaredRoute"), subtree.Attr("subtree"), subtree.Attr("exact")));
        Assert.Equal(["stock/main.go:14-14"], subtree.Lines());
        var home = result.Fact("operation:stock:GET /");
        Assert.Equal(("/", "GET /{$}", null, "true"), (home.Attr("path"), home.Attr("declaredRoute"), home.Attr("subtree"), home.Attr("exact")));
        Assert.Null(result.Fact("operation:stock:ANY /default-mux").Attr("subtree"));

        Assert.Equal([(20, "dynamic-route"), (21, "dynamic-route")], Notes(result));
    }

    [Fact]
    public void A_mux_that_is_given_to_StripPrefix_has_no_known_prefix()
    {
        var result = Stock("""
            package main

            import "net/http"

            func main() {
                api := http.NewServeMux()
                api.HandleFunc("GET /items", list)
                root := http.NewServeMux()
                root.Handle("/api/", http.StripPrefix("/api", api))
                root.HandleFunc("GET /healthz", health)
            }
            """);

        Assert.Equal(["operation:stock:ANY /api", "operation:stock:GET /healthz"], Operations(result));
        Assert.Equal([(7, "unresolved-route-prefix")], Notes(result));
    }

    // ---- go.chi-route -----------------------------------------------------------------

    [Fact]
    public void Chi_routes_take_the_prefix_of_route_blocks_and_of_mounts_in_the_same_function()
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
        var nested = result.Fact("operation:stock:GET /api/items/{sku}");
        Assert.Equal(["stock/main.go:17-17"], nested.Lines());
        Assert.Equal(("chi", "GET", "/api/items/{sku}", "/api/items/{}", "/api/items/{sku}/", "service:stock"),
            (nested.Attr("framework"), nested.Attr("method"), nested.Attr("path"), nested.Attr("routeTemplate"), nested.Attr("declaredRoute"), nested.Attr("service")));
        Assert.Equal(("go.chi-route", 2), (nested.Evidence[0].Rule, nested.Evidence[0].RuleVersion));
        var mounted = result.Fact("operation:stock:GET /admin/stats");
        Assert.Equal(("chi", "/admin/stats"), (mounted.Attr("framework"), mounted.Attr("declaredRoute")));
        Assert.Equal(["stock/main.go:25-25"], mounted.Lines());
        Assert.Equal(["stock/main.go:21-21"], result.Fact("operation:stock:PUT /api/bulk").Lines());

        // "r.With(auth).Delete" has a call as its receiver; a returned router and a router
        // parameter have a mount point that this file does not fix.
        Assert.Equal([(33, "unresolved-route-prefix"), (38, "unresolved-route-prefix")], Notes(result));
    }

    // ---- go.gin-route, go.echo-route --------------------------------------------------

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
        var create = gin.Fact("operation:stock:POST /v1/items/{sku}");
        Assert.Equal(("gin", "POST", "/v1/items/{sku}", "/v1/items/{}", "/v1/items/:sku"),
            (create.Attr("framework"), create.Attr("method"), create.Attr("path"), create.Attr("routeTemplate"), create.Attr("declaredRoute")));
        Assert.Equal(["stock/main.go:10-10"], create.Lines());
        Assert.Equal(("go.gin-route", 2), (create.Evidence[0].Rule, create.Evidence[0].RuleVersion));
        Assert.Equal(["stock/main.go:15-15"], gin.Fact("operation:stock:PATCH /items/{sku}").Lines());
        Assert.Equal([(19, "unresolved-route-prefix")], Notes(gin));

        var echo = Stock("""
            package main

            import "github.com/labstack/echo/v4"

            func main() {
                e := echo.New()
                api := e.Group("/api")
                api.GET("/users/:id", getUser)
                e.Add("DELETE", "/users/:id", deleteUser)
            }

            func extra(g *echo.Group) {
                g.GET("/in-unknown-group", nil)
            }
            """);

        Assert.Equal(["operation:stock:DELETE /users/{id}", "operation:stock:GET /api/users/{id}"], Operations(echo));
        var user = echo.Fact("operation:stock:GET /api/users/{id}");
        Assert.Equal(("echo", "GET", "/api/users/{id}", "/api/users/{}", "/api/users/:id"),
            (user.Attr("framework"), user.Attr("method"), user.Attr("path"), user.Attr("routeTemplate"), user.Attr("declaredRoute")));
        Assert.Equal(["stock/main.go:8-8"], user.Lines());
        Assert.Equal(("go.echo-route", 2), (user.Evidence[0].Rule, user.Evidence[0].RuleVersion));
        Assert.Equal([(13, "unresolved-route-prefix")], Notes(echo));
    }

    // ---- lexical scope (review item 5) ------------------------------------------------

    [Fact]
    public void A_router_method_name_on_a_receiver_that_is_not_a_router_in_scope_gives_no_route()
    {
        var result = Stock("""
            package main

            import (
                "net/http"

                "github.com/acme/apiclient"
                "github.com/gin-gonic/gin"
                "github.com/go-chi/chi/v5"
                "github.com/labstack/echo/v4"
            )

            func routes() {
                r := chi.NewRouter()
                r.Get("/chi", h)
                g := gin.Default()
                g.GET("/gin", h)
                e := echo.New()
                e.GET("/echo", h)
                mux := http.NewServeMux()
                mux.HandleFunc("GET /mux", h)
            }

            func callOut(r *apiclient.Client, g *apiclient.Client, e *apiclient.Client, mux *apiclient.Tree) {
                r.Post("/webhook", body)
                g.GET("/remote-gin", nil)
                e.GET("/remote-echo", nil)
                mux.HandleFunc("GET /remote-mux", nil)
            }

            func locals() {
                r := apiclient.New()
                r.Get("/local-client", nil)
                var g apiclient.Client
                g.GET("/local-var", nil)
            }

            func undeclared() {
                r.Get("/no-declaration-in-scope", nil)
            }

            func shadowedPackage(http *apiclient.Client, chi apiclient.Factory) {
                http.HandleFunc("/shadowed-package", nil)
                r := chi.NewRouter()
                r.Get("/from-shadowed-constructor", nil)
            }

            func (s *server) field() {
                s.r.Get("/struct-field", nil)
            }

            func resultType(e *apiclient.Client) func(e *gin.Engine) {
                e.GET("/parameter-not-the-name-in-the-result-type", nil)
                return func(e *gin.Engine) {
                    e.GET("/literal", h)
                }
            }
            """);

        Assert.Equal(
            [
                "operation:stock:GET /chi", "operation:stock:GET /echo", "operation:stock:GET /gin",
                "operation:stock:GET /literal", "operation:stock:GET /mux",
            ],
            Operations(result));
        Assert.Equal(["stock/main.go:54-54"], result.Fact("operation:stock:GET /literal").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void An_inner_declaration_hides_a_router_only_inside_its_block_or_function_literal()
    {
        var result = Stock("""
            package main

            import (
                "github.com/acme/apiclient"
                "github.com/go-chi/chi/v5"
            )

            func routes(ready bool) {
                r := chi.NewRouter()
                r.Get("/before", h)
                {
                    r := apiclient.New()
                    r.Get("/inner-block", nil)
                }
                go func(r *apiclient.Client) {
                    r.Get("/literal-parameter", nil)
                }(nil)
                go func() {
                    r.Get("/closure", h)
                }()
                switch {
                case ready:
                    r := apiclient.New()
                    r.Get("/first-clause", nil)
                default:
                    r.Get("/second-clause", h)
                }
                for _, r := range clients() {
                    r.Get("/loop-variable", nil)
                }
                if r, ok := lookup(); ok {
                    r.Get("/if-variable", nil)
                }
            }
            """);

        // The closure and the second clause see the router of the function. After the "for"
        // and "if" headers the name is hidden to the end of the block (wider than Go), so
        // nothing is read from it there.
        Assert.Equal(
            ["operation:stock:GET /before", "operation:stock:GET /closure", "operation:stock:GET /second-clause"],
            Operations(result));
        Assert.Equal(["stock/main.go:19-19"], result.Fact("operation:stock:GET /closure").Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_router_that_is_assigned_again_or_mounted_twice_has_no_known_prefix()
    {
        var result = Stock("""
            package main

            import (
                "github.com/gin-gonic/gin"
                "github.com/go-chi/chi/v5"
            )

            func groups(admin bool) {
                e := gin.New()
                g := e.Group("/public")
                if admin {
                    g = e.Group("/admin")
                }
                g.GET("/items", h)
                e = gin.Default()
                e.GET("/engine", h)
            }

            func mounts() {
                root := chi.NewRouter()
                shared := chi.NewRouter()
                shared.Get("/shared", h)
                root.Mount("/a", shared)
                root.Mount("/b", shared)
                once := chi.NewRouter()
                root.Mount("/once", once)
                once.Get("/after-mount", h)
            }
            """);

        // An engine is the root whatever is assigned to it; a group is where its last assignment put it.
        Assert.Equal(["operation:stock:GET /engine", "operation:stock:GET /once/after-mount"], Operations(result));
        Assert.Equal(["stock/main.go:27-27"], result.Fact("operation:stock:GET /once/after-mount").Lines());
        // Line 14 is g.GET("/items", h); line 22 is shared.Get("/shared", h).
        Assert.Equal([(14, "unresolved-route-prefix"), (22, "unresolved-route-prefix")], Notes(result));
    }

    [Fact]
    public void A_package_level_variable_counts_only_with_an_explicit_router_type()
    {
        var result = Stock("""
            package main

            import (
                "net/http"

                "github.com/acme/apiclient"
                "github.com/gin-gonic/gin"
                "github.com/go-chi/chi/v5"
            )

            var (
                mux    *http.ServeMux
                engine *gin.Engine
                router chi.Router
                client = apiclient.New()
            )

            func register() {
                mux.HandleFunc("GET /package-mux", h)
                engine.GET("/package-engine", h)
                router.Get("/package-chi", h)
                client.Get("/package-client", nil)
            }

            func shadow(mux *apiclient.Tree) {
                mux.HandleFunc("GET /shadowed", nil)
            }
            """);

        Assert.Equal(["operation:stock:GET /package-engine", "operation:stock:GET /package-mux"], Operations(result));
        Assert.Equal(["stock/main.go:19-19"], result.Fact("operation:stock:GET /package-mux").Lines());
        // A chi router at package level can be mounted from any file.
        Assert.Equal([(21, "unresolved-route-prefix")], Notes(result));
    }

    [Fact]
    public void A_constant_is_a_package_const_that_nothing_in_scope_hides_or_a_const_of_the_same_function()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/consts.go", """
                package main

                const (
                    itemsPattern = "GET /items"
                    ordersPattern string = "GET /orders"
                    builtPattern = "GET /" + "built"
                )
                """),
            ("stock/main.go", """
                package main

                import "net/http"

                func packageConstant(mux *http.ServeMux) {
                    mux.HandleFunc(itemsPattern, h)
                    mux.HandleFunc(ordersPattern, h)
                    mux.HandleFunc(builtPattern, h)
                }

                func parameterHides(mux *http.ServeMux, itemsPattern string) {
                    mux.HandleFunc(itemsPattern, h)
                }

                func localHides(mux *http.ServeMux) {
                    itemsPattern := os.Getenv("PATTERN")
                    mux.HandleFunc(itemsPattern, h)
                }

                func localConstant(mux *http.ServeMux) {
                    const localPattern = "GET /local"
                    mux.HandleFunc(localPattern, h)
                }

                func otherFunction(mux *http.ServeMux) {
                    mux.HandleFunc(localPattern, h)
                }
                """));

        Assert.Equal(["operation:stock:GET /items", "operation:stock:GET /local", "operation:stock:GET /orders"], Operations(result));
        Assert.Equal(["stock/main.go:6-6"], result.Fact("operation:stock:GET /items").Lines());
        Assert.Equal(["stock/main.go:22-22"], result.Fact("operation:stock:GET /local").Lines());
        Assert.Equal(
            [(8, "dynamic-route"), (12, "dynamic-route"), (17, "dynamic-route"), (26, "dynamic-route")],
            Notes(result));
    }

    [Fact]
    public void A_constant_with_two_values_in_one_package_is_not_resolved()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/a.go", "package main\n\nimport \"net/http\"\n\nconst pattern = \"GET /a\"\n\nfunc a() {\n\thttp.HandleFunc(pattern, nil)\n}\n"),
            ("stock/b.go", "//go:build other\n\npackage main\n\nconst pattern = \"GET /b\"\n"));

        Assert.Empty(Operations(result));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-route", "stock/a.go", 8)], result.Notes);
    }

    // ---- AMQP -------------------------------------------------------------------------

    [Fact]
    public void Each_amqp_call_on_a_channel_of_a_dialled_connection_gives_its_fact()
    {
        var result = Stock("""
            package main

            import (
                "context"

                amqp "github.com/rabbitmq/amqp091-go"
            )

            const deadLetters = "stock.dlx"

            func topology(url string) error {
                conn, err := amqp.Dial(url)
                if err != nil {
                    return err
                }
                ch, err := conn.Channel()
                if err != nil {
                    return err
                }
                ch.ExchangeDeclare("stock-events", "topic", true, false, false, false, nil)
                ch.QueueDeclare("stock.audit", true, false, false, false, amqp.Table{
                    "x-dead-letter-exchange":    deadLetters,
                    "x-dead-letter-routing-key": "audit.dead",
                })
                ch.QueueBind("stock.audit", "stock.#", "stock-events", false, nil)
                ch.Publish("stock-events", "stock.reserved", false, false, amqp.Publishing{})
                ch.PublishWithContext(context.Background(), "", "audit-queue", false, false, amqp.Publishing{})
                ch.Consume("stock.audit", "", false, false, false, false, nil)
                return nil
            }
            """, "broker.go");

        var exchange = result.Fact("channel:amqp:stock-events");
        Assert.Equal((FactKind.Channel, "stock-events"), (exchange.Kind, exchange.Name));
        Assert.Equal(("exchange", "amqp", "topic", "true"),
            (exchange.Attr("kind"), exchange.Attr("transport"), exchange.Attr("amqp.exchange.type"), exchange.Attr("durable")));
        Assert.Equal(["stock/broker.go:20-20"], exchange.Lines());
        Assert.Equal(("go.amqp-exchange-declare", 2), (exchange.Evidence[0].Rule, exchange.Evidence[0].RuleVersion));

        var queue = result.Fact("channel:amqp:stock.audit");
        Assert.Equal(("queue", "amqp", "true", "channel:amqp:stock.dlx", "audit.dead"),
            (queue.Attr("kind"), queue.Attr("transport"), queue.Attr("durable"), queue.Attr("deadLetterExchange"), queue.Attr("deadLetterRoutingKey")));
        Assert.Equal(["stock/broker.go:21-24"], queue.Lines());
        Assert.Equal("go.amqp-queue-declare", queue.Evidence[0].Rule);

        var binding = result.Fact("binding:stock:amqp:stock.audit:stock-events:stock.#");
        Assert.Equal(FactKind.Subscription, binding.Kind);
        Assert.Equal(("amqp", "channel:amqp:stock.audit", "channel:amqp:stock-events", "stock.#", "service:stock"),
            (binding.Attr("transport"), binding.Attr("queue"), binding.Attr("exchange"), binding.Attr("routingKey"), binding.Attr("service")));
        Assert.Equal(["stock/broker.go:25-25"], binding.Lines());
        Assert.Equal("go.amqp-queue-bind", binding.Evidence[0].Rule);

        var publication = result.Fact("publication:stock:amqp:stock-events:stock.reserved");
        Assert.Equal(FactKind.Publication, publication.Kind);
        Assert.Equal(("amqp", "channel:amqp:stock-events", "stock-events", "stock.reserved", "service:stock"),
            (publication.Attr("transport"), publication.Attr("channel"), publication.Attr("exchange"), publication.Attr("routingKey"), publication.Attr("service")));
        Assert.Equal(["stock/broker.go:26-26"], publication.Lines());
        Assert.Equal("go.amqp-publish", publication.Evidence[0].Rule);
        // The default exchange routes to the queue that the routing key names.
        var direct = result.Fact("publication:stock:amqp::audit-queue");
        Assert.Equal(("channel:amqp:audit-queue", "(default)"), (direct.Attr("channel"), direct.Attr("exchange")));
        Assert.Equal(["stock/broker.go:27-27"], direct.Lines());

        var subscription = result.Fact("subscription:stock:amqp:stock.audit");
        Assert.Equal(FactKind.Subscription, subscription.Kind);
        Assert.Equal(("amqp", "channel:amqp:stock.audit", "service:stock", null),
            (subscription.Attr("transport"), subscription.Attr("queue"), subscription.Attr("service"), subscription.Attr("message")));
        Assert.Equal(["stock/broker.go:28-28"], subscription.Lines());
        Assert.Equal("go.amqp-consume", subscription.Evidence[0].Rule);

        Assert.Equal(6, result.Facts.Count(f => f.Kind is FactKind.Channel or FactKind.Publication or FactKind.Subscription));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Amqp_names_that_are_not_static_are_noted()
    {
        var result = Stock("""
            package main

            import amqp "github.com/rabbitmq/amqp091-go"

            func publish(ctx context.Context, ch *amqp.Channel, key string) {
                ch.PublishWithContext(ctx, "stock-events", "stock.reserved", false, false, amqp.Publishing{})
                ch.PublishWithContext(ctx, "stock-events", key, false, false, amqp.Publishing{})
                ch.QueueDeclare("", false, true, true, false, nil)
                ch.ConsumeWithContext(ctx, "stock.audit", "", false, false, false, false, nil)
            }
            """, "amqp.go");

        Assert.Equal(
            ["publication:stock:amqp:stock-events:stock.reserved", "subscription:stock:amqp:stock.audit"],
            Keys(result, FactKind.Publication, FactKind.Subscription, FactKind.Channel));
        Assert.Equal(["stock/amqp.go:9-9"], result.Fact("subscription:stock:amqp:stock.audit").Lines());
        Assert.Equal([(7, "dynamic-channel-name"), (8, "dynamic-channel-name")], Notes(result));
    }

    // ---- brokers: receivers in scope (review item 11) ---------------------------------

    [Fact]
    public void A_broker_method_name_on_an_unknown_receiver_gives_nothing()
    {
        var result = Stock("""
            package main

            import (
                "database/sql"

                "github.com/nats-io/nats.go"
                amqp "github.com/rabbitmq/amqp091-go"
            )

            type eventBus struct{}

            func (eventBus) Publish(subject string, data []byte) {}

            func emit(bus eventBus, ch fakeChannel, nc *natsLike) {
                bus.Publish("local-event", nil)
                ch.Publish("local-exchange", "key", false, false, nil)
                ch.Consume("local-queue", "", false, false, false, false, nil)
                ch.ExchangeDeclare("local-exchange", "topic", true, false, false, false, nil)
                nc.Subscribe("local.subject", nil)
            }

            func real(nc *nats.Conn, ch *amqp.Channel) {
                nc.Publish("real.subject", nil)
                {
                    nc := eventBus{}
                    nc.Publish("shadowed.subject", nil)
                }
                func(ch fakeChannel) {
                    ch.Consume("shadowed-queue", "", false, false, false, false, nil)
                }(fakeChannel{})
            }

            func stores(sql queryBuilder) {
                sql.Open("mysql", "not a database/sql call")
            }
            """, "bus.go");

        Assert.Equal(
            ["channel:nats:real.subject", "publication:stock:nats:real.subject"],
            Keys(result, FactKind.Channel, FactKind.Publication, FactKind.Subscription, FactKind.DataAccess));
        Assert.Equal(["stock/bus.go:23-23"], result.Fact("publication:stock:nats:real.subject").Lines());
        Assert.Empty(result.Notes);
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

    // ---- go.kafka-topic, go.nats-subject ----------------------------------------------

    [Fact]
    public void Kafka_topics_and_nats_subjects_are_channels_with_publications_and_subscriptions()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
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

                func subjects(url string, subject string) {
                    nc, err := nats.Connect(url)
                    if err != nil {
                        return
                    }
                    nc.Subscribe("stock.>", handle)
                    nc.QueueSubscribe("stock.audit", "auditors", handle)
                    nc.Publish("stock.low", nil)
                    nc.Publish(subject, nil)
                }
                """));

        var topic = result.Fact("channel:kafka:stock.reserved.v1");
        Assert.Equal((FactKind.Channel, "stock.reserved.v1", "topic", "kafka"), (topic.Kind, topic.Name, topic.Attr("kind"), topic.Attr("transport")));
        Assert.Equal(["stock/kafka.go:10-10"], topic.Lines());
        Assert.Equal(("go.kafka-topic", 2), (topic.Evidence[0].Rule, topic.Evidence[0].RuleVersion));
        var reads = result.Fact("subscription:stock:kafka:stock.reserved.v1");
        Assert.Equal(("kafka", "channel:kafka:stock.reserved.v1", "service:stock"), (reads.Attr("transport"), reads.Attr("channel"), reads.Attr("service")));
        Assert.Equal(["stock/kafka.go:10-10"], reads.Lines());
        var writes = result.Fact("publication:stock:kafka:stock.released.v1");
        Assert.Equal("channel:kafka:stock.released.v1", writes.Attr("channel"));
        Assert.Equal(["stock/kafka.go:13-13"], writes.Lines());

        var subject = result.Fact("channel:nats:stock.>");
        Assert.Equal(("subject", "nats"), (subject.Attr("kind"), subject.Attr("transport")));
        Assert.Equal(["stock/nats.go:10-10"], subject.Lines());
        Assert.Equal(("go.nats-subject", 2), (subject.Evidence[0].Rule, subject.Evidence[0].RuleVersion));
        Assert.Equal("channel:nats:stock.>", result.Fact("subscription:stock:nats:stock.>").Attr("channel"));
        Assert.Equal(["stock/nats.go:11-11"], result.Fact("subscription:stock:nats:stock.audit").Lines());
        var low = result.Fact("publication:stock:nats:stock.low");
        Assert.Equal(("nats", "channel:nats:stock.low", "service:stock"), (low.Attr("transport"), low.Attr("channel"), low.Attr("service")));
        Assert.Equal(["stock/nats.go:12-12"], low.Lines());

        Assert.Equal(
            [("stock/kafka.go", 14, "dynamic-channel-name"), ("stock/nats.go", 13, "dynamic-channel-name")],
            result.Notes.Select(n => (n.Path, n.Line, n.Reason)));
    }

    [Fact]
    public void Confluent_topics_are_read_from_a_consumer_in_scope()
    {
        var result = Stock("""
            package main

            import "github.com/confluentinc/confluent-kafka-go/v2/kafka"

            const auditTopic = "stock.audit.v1"

            func consume(c *kafka.Consumer, p *kafka.Producer, other fakeConsumer, topic string) {
                c.SubscribeTopics([]string{"stock.reserved.v1", auditTopic}, nil)
                other.SubscribeTopics([]string{"not-kafka"}, nil)
                p.Produce(&kafka.Message{TopicPartition: kafka.TopicPartition{Topic: &topic, Partition: kafka.PartitionAny}}, nil)
            }
            """, "confluent.go");

        Assert.Equal(
            ["channel:kafka:stock.audit.v1", "channel:kafka:stock.reserved.v1", "subscription:stock:kafka:stock.audit.v1", "subscription:stock:kafka:stock.reserved.v1"],
            Keys(result, FactKind.Channel, FactKind.Publication, FactKind.Subscription));
        Assert.Equal(["stock/confluent.go:8-8"], result.Fact("subscription:stock:kafka:stock.reserved.v1").Lines());
        // A topic passed as a pointer to a variable is not known from the text.
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "dynamic-channel-name", "stock/confluent.go", 10)], result.Notes);
    }

    // ---- go.sql-open ------------------------------------------------------------------

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
        Assert.Equal(("database/sql", "mysql", "mysql", "service:stock", "stock/go.mod"),
            (mysql.Attr("technology"), mysql.Attr("driver"), mysql.Attr("storeKind"), mysql.Attr("service"), mysql.Attr("project")));
        Assert.Equal(["stock/main.go:11-11"], mysql.Lines());
        Assert.Equal(("go.sql-open", 3), (mysql.Evidence[0].Rule, mysql.Evidence[0].RuleVersion));
        Assert.Null(mysql.Attr("connectionVariable"));
        Assert.Equal("sqlite", result.Fact("dataaccess:stock:sql:sqlite3").Attr("storeKind"));
        var pgx = result.Fact("dataaccess:stock:pgx");
        Assert.Equal(("pgx", "postgres"), (pgx.Attr("technology"), pgx.Attr("storeKind")));
        Assert.Equal(["stock/main.go:13-13"], pgx.Lines());
        Assert.Equal("unknown", result.Fact("dataaccess:stock:sql:odbc").Attr("storeKind"));
        Assert.Equal([(14, "dynamic-driver-name")], Notes(result));
        Assert.DoesNotContain("app:pw", ExtractionJson.Serialize(result));
    }

    // ---- go.struct-message ------------------------------------------------------------

    [Fact]
    public void A_struct_decoded_from_a_consumed_amqp_delivery_is_a_message_with_its_json_fields()
    {
        var result = Stock("""
            package main

            import (
                "encoding/json"

                amqp "github.com/rabbitmq/amqp091-go"
            )

            type line struct {
                SKU      string `json:"sku"`
                Quantity int    `json:"quantity,omitempty"`
            }

            type reserved struct {
                OrderID string            `json:"orderId"`
                Lines   []line            `json:"lines"`
                Labels  map[string]string `json:"labels"`
                Note    *string
                Skipped string `json:"-"`
                private int
            }

            func consume(ch *amqp.Channel) {
                deliveries, _ := ch.Consume("stock.reserved", "", false, false, false, false, nil)
                for delivery := range deliveries {
                    var event reserved
                    if err := json.Unmarshal(delivery.Body, &event); err != nil {
                        continue
                    }
                }
            }
            """, "consumer.go");

        Assert.Equal(
            ["message:github.com/acme/shop/stock/v2.line", "message:github.com/acme/shop/stock/v2.reserved"],
            Keys(result, FactKind.Message));

        var message = result.Fact("message:github.com/acme/shop/stock/v2.reserved");
        Assert.Equal("reserved", message.Name);
        Assert.Equal("Note:*string;labels:map[string]string;lines:[]line;orderId:string", message.Attr("fields"));
        Assert.Equal(("go", "github.com/acme/shop/stock/v2.reserved", "stock/go.mod"),
            (message.Attr("language"), message.Attr("goType"), message.Attr("declaredIn")));
        // The struct declaration, and the decode that makes it a message.
        Assert.Equal(["stock/consumer.go:14-21", "stock/consumer.go:27-27"], message.Lines());
        Assert.All(message.Evidence, e => Assert.Equal(("go.struct-message", 1), (e.Rule, e.RuleVersion)));

        var nested = result.Fact("message:github.com/acme/shop/stock/v2.line");
        Assert.Equal("quantity:int;sku:string", nested.Attr("fields"));
        Assert.Equal(["stock/consumer.go:9-12"], nested.Lines());

        var subscription = result.Fact("subscription:stock:amqp:stock.reserved");
        Assert.Equal("message:github.com/acme/shop/stock/v2.reserved", subscription.Attr("message"));
        Assert.Equal(["stock/consumer.go:24-24", "stock/consumer.go:27-27"], subscription.Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_struct_decoded_in_a_nats_handler_or_from_a_kafka_reader_is_a_message_of_its_package()
    {
        var result = Repo.Extract(
            ("stock/go.mod", GoMod),
            ("stock/internal/events/events.go", """
                package events

                import (
                    "context"
                    "encoding/json"

                    "github.com/nats-io/nats.go"
                    "github.com/segmentio/kafka-go"
                )

                type Released struct {
                    SKU string `json:"sku"`
                }

                func subscribe(nc *nats.Conn) {
                    nc.Subscribe("stock.released", func(m *nats.Msg) {
                        released := Released{}
                        _ = json.Unmarshal(m.Data, &released)
                    })
                }

                func read(ctx context.Context) {
                    reader := kafka.NewReader(kafka.ReaderConfig{Topic: "stock.released.v1", GroupID: "stock"})
                    message, err := reader.ReadMessage(ctx)
                    if err != nil {
                        return
                    }
                    var released Released
                    _ = json.Unmarshal(message.Value, &released)
                }
                """));

        const string key = "message:github.com/acme/shop/stock/v2/internal/events.Released";
        Assert.Equal([key], Keys(result, FactKind.Message));
        var message = result.Fact(key);
        Assert.Equal(("sku:string", "github.com/acme/shop/stock/v2/internal/events.Released"), (message.Attr("fields"), message.Attr("goType")));
        Assert.Equal(
            ["stock/internal/events/events.go:11-13", "stock/internal/events/events.go:18-18", "stock/internal/events/events.go:29-29"],
            message.Lines());

        var nats = result.Fact("subscription:stock:nats:stock.released");
        Assert.Equal(key, nats.Attr("message"));
        Assert.Equal(["stock/internal/events/events.go:16-19", "stock/internal/events/events.go:18-18"], nats.Lines());
        var kafka = result.Fact("subscription:stock:kafka:stock.released.v1");
        Assert.Equal(key, kafka.Attr("message"));
        Assert.Equal(["stock/internal/events/events.go:23-23", "stock/internal/events/events.go:29-29"], kafka.Lines());
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_decode_that_does_not_meet_the_exact_rule_gives_no_message()
    {
        var result = Stock("""
            package main

            import (
                "encoding/json"

                "github.com/acme/contracts"
                amqp "github.com/rabbitmq/amqp091-go"
            )

            type reserved struct {
                OrderID string `json:"orderId"`
            }

            type withEmbedded struct {
                reserved
                Extra string `json:"extra"`
            }

            func helper(delivery amqp.Delivery, body []byte) {
                var event reserved
                json.Unmarshal(delivery.Body, &event)
                json.Unmarshal(body, &event)
            }

            func consume(ch *amqp.Channel) {
                deliveries, _ := ch.Consume("stock.reserved", "", false, false, false, false, nil)
                for delivery := range deliveries {
                    var raw map[string]any
                    json.Unmarshal(delivery.Body, &raw)
                    var foreign contracts.Reserved
                    json.Unmarshal(delivery.Body, &foreign)
                    var embedded withEmbedded
                    json.Unmarshal(delivery.Body, &embedded)
                    type reserved struct{ Local int }
                    var local reserved
                    json.Unmarshal(delivery.Body, &local)
                    go func() {
                        var event reserved
                        json.Unmarshal(delivery.Body, &event)
                    }()
                    json.Unmarshal(delivery.Headers, &raw)
                }
            }
            """, "consumer.go");

        Assert.Empty(Keys(result, FactKind.Message));
        Assert.Null(result.Fact("subscription:stock:amqp:stock.reserved").Attr("message"));
        // The helper's delivery is not tied to a consume call: nothing is known about it, and
        // nothing is noted. Each decode of the consumed delivery that has no exact type is noted.
        Assert.Equal(
            [(29, "undecided-message-type"), (31, "undecided-message-type"), (33, "undecided-message-type"),
                (36, "undecided-message-type"), (39, "undecided-message-type")],
            Notes(result));
    }

    [Fact]
    public void The_go_worker_of_the_fixture_shows_the_renamed_field_in_its_message_contract()
    {
        var before = EvidenceExtractor.Extract(Repo.Load("orders"));
        var after = EvidenceExtractor.Extract(Repo.Load("orders-outbox-removed"));
        const string envelope = "message:github.com/acme/orders/inventory-worker.envelope";
        const string created = "message:github.com/acme/orders/inventory-worker.orderCreated";

        foreach (var result in new[] { before, after })
        {
            Assert.Equal("message:orderCreated;messageId:string", result.Fact(envelope).Attr("fields"));
            Assert.Equal(["inventory-worker/consumer.go:26-29", "inventory-worker/consumer.go:63-63"], result.Fact(envelope).Lines());
            Assert.Equal(["inventory-worker/consumer.go:19-23"], result.Fact(created).Lines());
            var subscription = result.Fact("subscription:inventory-worker:amqp:inventory.order-created");
            Assert.Equal(envelope, subscription.Attr("message"));
            Assert.Equal(["inventory-worker/consumer.go:50-50", "inventory-worker/consumer.go:63-63"], subscription.Lines());
        }
        Assert.Equal("customerId:string;orderId:string;total:float64", before.Fact(created).Attr("fields"));
        Assert.Equal("customerId:string;orderId:string;totalAmount:float64", after.Fact(created).Attr("fields"));
    }

    // ---- bounded work (review item 17) ------------------------------------------------

    private const int Bound = 256 * 1024 - 64;
    private const string Chi = "package main\n\nimport \"github.com/go-chi/chi/v5\"\n\n";
    private const string Gin = "package main\n\nimport \"github.com/gin-gonic/gin\"\n\n";
    private const string Http = "package main\n\nimport \"net/http\"\n\n";

    /// <summary>The head, then as many units as fit under the file bound, then the tail.</summary>
    private static string Fill(string head, Func<int, string> unit, string tail = "")
    {
        var builder = new StringBuilder(head);
        for (var i = 0; ; i++)
        {
            var next = unit(i);
            if (builder.Length + next.Length + tail.Length > Bound)
                break;
            builder.Append(next);
        }
        return builder.Append(tail).ToString();
    }

    /// <summary>The head, then N openers and N closers, as many as fit under the file bound, then the tail.</summary>
    private static string Nested(string head, string open, string close, string tail = "")
    {
        var count = (Bound - head.Length - tail.Length) / (open.Length + close.Length);
        var builder = new StringBuilder(head);
        for (var i = 0; i < count; i++)
            builder.Append(open);
        for (var i = 0; i < count; i++)
            builder.Append(close);
        return builder.Append(tail).ToString();
    }

    private static (ExtractionResult Result, long Work) Bounded(string content, string path = "svc/main.go") =>
        path == "svc/go.mod"
            ? Repo.ExtractBounded(path, content)
            : Repo.ExtractBounded(path, content, ("svc/go.mod", "module github.com/acme/svc\n"));

    /// <summary>A linear reader spends a few operations for each character; the budget is 64.</summary>
    private static void AssertLinear(ExtractionResult result, long work, int operationsPerByte = 12)
    {
        Assert.DoesNotContain("work-limit", result.Reasons());
        Assert.InRange(work, 1, (long)operationsPerByte * Bound);
    }

    [Theory]
    [InlineData("const (\n", ")\n")]
    [InlineData("const (\na = \"b\"\n", ")\n")]
    [InlineData("const(", ")")]
    [InlineData("var (\n", ")\n")]
    [InlineData("type (\n", ")\n")]
    public void Nested_declaration_groups_at_the_file_bound_are_read_in_linear_work(string open, string close)
    {
        var (result, work) = Bounded(Nested("package main\n\nimport \"net/http\"\n\nfunc main() {\n\thttp.HandleFunc(\"GET /real\", nil)\n}\n", open, close));

        AssertLinear(result, work);
        Assert.True(result.IsComplete);
        Assert.Equal(["operation:svc:GET /real"], Operations(result));
    }

    [Fact]
    public void Many_routers_and_many_route_calls_are_read_in_linear_work()
    {
        // 2500 routers, then a route on each of them in turn for the rest of the file.
        var (result, work) = Bounded(Fill(Chi + "func main() {\n",
            i => i < 2500 ? $"\tr{i} := chi.NewRouter()\n" : $"\tr{i % 2500}.Get(\"/a{i}\", h)\n", "}\n"));

        AssertLinear(result, work);
        // Every router is followed; the facts of one file are capped by the context, and that is reported.
        Assert.Equal(ExtractionLimits.Default.MaxFactsPerFile, result.Facts.Count(f => f.Kind == FactKind.Operation));
        Assert.Equal("GET /a2500", result.Fact("operation:svc:GET /a2500").Name);
        Assert.Equal(["limit:facts-per-file"], result.Reasons());
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void A_router_declared_before_each_route_call_is_read_in_linear_work()
    {
        var (result, work) = Bounded(Fill(Chi + "func main() {\n",
            i => i % 2 == 0 ? $"\tr{i} := chi.NewRouter()\n" : $"\tr{i - 1}.Get(\"/a{i}\", h)\n", "}\n"));

        AssertLinear(result, work);
        Assert.Equal(ExtractionLimits.Default.MaxFactsPerFile, result.Facts.Count(f => f.Kind == FactKind.Operation));
        Assert.Equal(["limit:facts-per-file"], result.Reasons());
    }

    [Fact]
    public void Deeply_nested_function_literals_are_read_in_linear_work()
    {
        var (result, work) = Bounded(Nested(Chi + "func main() {\n\tr := chi.NewRouter()\n\tr.Get(\"/real\", h)\n}\nvar x = ", "func(r int){", "}"));

        AssertLinear(result, work);
        Assert.True(result.IsComplete);
        Assert.Equal(["operation:svc:GET /real"], Operations(result));
    }

    [Fact]
    public void Deeply_nested_route_groups_stop_at_the_maximum_router_depth()
    {
        var (result, work) = Bounded(Nested(Chi + "func main() {\n\tr := chi.NewRouter()\n",
            "r.Route(\"/a\", func(r chi.Router) {\nr.Get(\"/x\", h)\n", "})\n", "}\n"));

        AssertLinear(result, work);
        // 32 routers inside each other have a prefix: "/a/x" to 32 times "/a" and "/x". The 33rd is noted once.
        Assert.Equal(GoSourceReader.MaxRouterDepth, result.Facts.Count(f => f.Kind == FactKind.Operation));
        Assert.True(result.Has("operation:svc:GET /a/x"));
        Assert.True(result.Has("operation:svc:GET " + string.Concat(Enumerable.Repeat("/a", 32)) + "/x"));
        Assert.Single(result.Notes, n => n.Reason == "router-nesting-too-deep");
        // The k-th "r.Route(" is on line 5 + 2k; the 33rd makes the router that is too deep.
        Assert.Equal(71, result.Notes.Single(n => n.Reason == "router-nesting-too-deep").Line);
        Assert.Contains("unresolved-route-prefix", result.Reasons());
    }

    [Fact]
    public void A_long_chain_of_group_assignments_stops_at_the_maximum_router_depth()
    {
        var (result, work) = Bounded(Fill(Gin + "func main() {\n\tg0 := gin.Default()\n",
            i => $"\tg{i + 1} := g{i}.Group(\"/a\")\n\tg{i + 1}.GET(\"/x\", h)\n", "}\n"));

        AssertLinear(result, work);
        Assert.Equal(GoSourceReader.MaxRouterDepth, result.Facts.Count(f => f.Kind == FactKind.Operation));
        Assert.True(result.Has("operation:svc:GET " + string.Concat(Enumerable.Repeat("/a", 32)) + "/x"));
        // "g1 := g0.Group" is on line 7, so "g33 := g32.Group" is on line 7 + 2 * 32.
        Assert.Equal(71, result.Notes.Single(n => n.Reason == "router-nesting-too-deep").Line);
    }

    [Fact]
    public void Many_StripPrefix_calls_are_read_in_linear_work()
    {
        var (result, work) = Bounded(Fill(Http + "func main() {\n\tmux := http.NewServeMux()\n\tmux.HandleFunc(\"GET /inner\", h)\n",
            i => $"\thttp.Handle(\"/p{i}/\", http.StripPrefix(\"/p{i}\", mux))\n", "}\n"));

        AssertLinear(result, work);
        Assert.False(result.Has("operation:svc:GET /inner"));
        Assert.Equal("true", result.Fact("operation:svc:ANY /p0").Attr("subtree"));
        Assert.Contains(result.Notes, n => n is { Reason: "unresolved-route-prefix", Line: 7 });
    }

    [Fact]
    public void Nested_StripPrefix_and_nested_route_calls_are_read_in_linear_work()
    {
        var (strip, stripWork) = Bounded(Nested(Http + "func main() {\n\tmux := http.NewServeMux()\n\tmux.HandleFunc(\"GET /real\", h)\n}\nvar x = ", "http.StripPrefix(\"/p\", ", ")"));
        AssertLinear(strip, stripWork);
        Assert.Equal(["operation:svc:GET /real"], Operations(strip));

        var (calls, callsWork) = Bounded(Nested(Chi + "func main() {\n\tr := chi.NewRouter()\n\t", "r.Get(\"/a\", ", ")", "\n}\n"));
        AssertLinear(calls, callsWork);
        Assert.True(calls.IsComplete);
        // Every call of the nest declares the same route.
        Assert.Equal(["operation:svc:GET /a"], Operations(calls));
    }

    [Fact]
    public void Many_imports_requirements_and_kafka_topics_are_read_in_linear_work()
    {
        var (imports, importsWork) = Bounded(Fill("package main\n\nimport (\n\t\"net/http\"\n", i => $"\tp{i} \"example.com/p{i}\"\n", ")\n\nfunc main() {\n\thttp.HandleFunc(\"GET /real\", nil)\n}\n"));
        AssertLinear(imports, importsWork);
        Assert.Equal(["operation:svc:GET /real"], Operations(imports));

        var (requires, requiresWork) = Bounded(Fill("module github.com/acme/svc\nrequire (\n", i => $"\texample.com/m{i} v1.0.0\n", ")\n"), "svc/go.mod");
        AssertLinear(requires, requiresWork);
        // The service is kept; the requirements beyond the bound of one file are reported as left out.
        Assert.True(requires.Has("service:svc"));
        Assert.Equal(ExtractionLimits.Default.MaxFactsPerFile - 1, requires.Facts.Count(f => f.Kind == FactKind.Dependency));
        Assert.Equal(["limit:facts-per-file"], requires.Reasons());

        var (topics, topicsWork) = Bounded(Fill("package main\n\nimport \"github.com/segmentio/kafka-go\"\n\nfunc main() {\n", i => $"\t_ = kafka.Message{{Topic: \"t{i}\"}}\n", "}\n"));
        AssertLinear(topics, topicsWork);
        Assert.True(topics.Has("publication:svc:kafka:t0"));
    }

    [Fact]
    public void Many_declarations_and_many_decodes_are_read_in_linear_work()
    {
        // Every line declares a name again and resolves it: a lookup that walks its declarations is quadratic here.
        var (shadows, shadowsWork) = Bounded(Fill(Chi + "func main() {\n\tr := chi.NewRouter()\n",
            i => "\t{ r := x(); r.Get(\"/no\", h) }\n", "\tr.Get(\"/real\", h)\n}\n"));
        AssertLinear(shadows, shadowsWork);
        Assert.Equal(["operation:svc:GET /real"], Operations(shadows));

        const string head = """
            package main

            import (
                "encoding/json"

                amqp "github.com/rabbitmq/amqp091-go"
            )

            type event struct {
                ID string `json:"id"`
                Next *event
            }

            func consume(ch *amqp.Channel) {
                deliveries, _ := ch.Consume("q", "", false, false, false, false, nil)
                for d := range deliveries {
                    var e event

            """;
        var (decodes, decodesWork) = Bounded(Fill(head, _ => "json.Unmarshal(d.Body, &e)\n", "}\n}\n"));
        AssertLinear(decodes, decodesWork);
        Assert.Equal("Next:*event;id:string", decodes.Fact("message:github.com/acme/svc.event").Attr("fields"));
        Assert.Equal("message:github.com/acme/svc.event", decodes.Fact("subscription:svc:amqp:q").Attr("message"));
    }
}
