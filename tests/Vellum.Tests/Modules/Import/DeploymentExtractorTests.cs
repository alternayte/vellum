using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>Compose, Dockerfile, Kubernetes and workflow rules, in the forms the Orders fixture does not use.</summary>
public class DeploymentExtractorTests
{
    private static (string, int)[] Rules(ExtractedFact fact) =>
        fact.Evidence.Select(e => (e.Rule, e.RuleVersion)).Distinct().ToArray();

    // ---- compose -----------------------------------------------------------------------

    [Fact]
    public void Compose_services_keep_names_and_never_environment_values()
    {
        var result = Repo.Extract(("deploy/compose.yaml", """
            services:
              cache:
                image: docker.io/bitnami/redis:7.2@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                environment:
                  - REDIS_PASSWORD=not-in-any-fact
                  - ALLOW_EMPTY_PASSWORD
              events:
                image: confluentinc/cp-kafka:7.6.0
                ports:
                  - target: 9092
                    published: 19092
                  - 9093
              search:
                image: ghcr.io/acme/custom-search:2
                depends_on:
                  cache:
                    condition: service_healthy
              web:
                build: ./web
                environment:
                  API_URL: http://api:8080
                  SESSION_KEY: not-in-any-fact-either
            volumes:
              data: {}
            x-registry-password: not-in-any-fact
            """));

        // The key holds the compose project, which is the directory of the file.
        var cache = result.Fact("datastore:deploy:cache");
        Assert.Equal(FactKind.DataStore, cache.Kind);
        Assert.Equal("cache", cache.Name);
        Assert.Equal(("compose", "redis"), (cache.Attr("source"), cache.Attr("kind")));
        Assert.Equal("ALLOW_EMPTY_PASSWORD,REDIS_PASSWORD", cache.Attr("environment"));
        // An image digest is public by design and stays.
        Assert.Equal("docker.io/bitnami/redis:7.2@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", cache.Attr("image"));
        Assert.Equal(["deploy/compose.yaml:2-6"], cache.Lines());
        Assert.Equal([("compose.service", 2)], Rules(cache));

        var events = result.Fact("deployment:compose:deploy:events");
        Assert.Equal(FactKind.Deployment, events.Kind);
        Assert.Equal(("kafka", "broker", "19092:9092,9093"), (events.Attr("kind"), events.Attr("role"), events.Attr("ports")));
        Assert.Equal(["deploy/compose.yaml:7-12"], events.Lines());

        var search = result.Fact("deployment:compose:deploy:search");
        Assert.Equal(("unknown", "service", "cache"), (search.Attr("kind"), search.Attr("role"), search.Attr("dependsOn")));

        var web = result.Fact("deployment:compose:deploy:web");
        Assert.Equal(("./web", "API_URL,SESSION_KEY", "unknown"), (web.Attr("build"), web.Attr("environment"), web.Attr("kind")));
        Assert.Equal(["deploy/compose.yaml:18-22"], web.Lines());

        Assert.Equal(4, result.Facts.Count);
        Assert.Empty(result.Notes);
        Assert.DoesNotContain("not-in-any-fact", ExtractionJson.Serialize(result));
        Assert.DoesNotContain("http://api:8080", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void A_yaml_file_that_is_not_named_as_a_compose_file_is_not_read_as_one()
    {
        var result = Repo.Extract(("config/services.yaml", "services:\n  db:\n    image: postgres:16\n"));

        Assert.Empty(result.Facts);
    }

    [Fact]
    public void A_compose_command_keeps_the_executable_and_the_flag_names_only()
    {
        var result = Repo.Extract(("compose.yaml", """
            services:
              cache:
                image: redis:7
                command: redis-server --requirepass hunter2-a --port=6380
              queue:
                image: rabbitmq:3
                entrypoint: ["/usr/local/bin/start", "--cookie", "hunter2-b", "--name=hunter2-c"]
                command: ["--password", "hunter2-d", "-p", "hunter2-e", "hunter2-f"]
              api:
                build: .
                command: PASSWORD=hunter2-g ./api --token=hunter2-h serve hunter2-i -phunter2-j
            """));

        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
        // The string form: a value after a space and a value after '='.
        Assert.Equal("redis-server --requirepass --port", result.Fact("datastore:.:cache").Attr("command"));
        // The list form. With an entrypoint, the command is its argument list and has no executable.
        var queue = result.Fact("deployment:compose:.:queue");
        Assert.Equal(("/usr/local/bin/start --cookie --name", "--password -p"), (queue.Attr("entrypoint"), queue.Attr("command")));
        // A leading assignment is not the executable. "-phunter2-j" can be a flag with its value and is not kept.
        Assert.Equal("./api --token", result.Fact("deployment:compose:.:api").Attr("command"));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void An_infrastructure_image_that_runs_a_client_tool_or_once_is_not_a_data_store()
    {
        var result = Repo.Extract(("compose.yaml", """
            services:
              db:
                image: postgres:16
              backup:
                image: postgres:16
                restart: "no"
                command: ["sh", "-c", "pg_dump -h db -U app app > /backup/app.sql"]
              db-migrate:
                image: postgres:16
              ready:
                image: postgres:16
                entrypoint: pg_isready -h db
              runonce:
                image: redis:7
                restart: "no"
                command: redis-server --save ""
              broker-setup:
                image: rabbitmq:3
                command: rabbitmqadmin declare queue name=jobs
            """));

        Assert.Equal(FactKind.DataStore, result.Fact("datastore:.:db").Kind);
        Assert.Equal("postgres", result.Fact("datastore:.:db").Attr("kind"));
        foreach (var name in (string[])["backup", "db-migrate", "ready", "runonce", "broker-setup"])
        {
            Assert.False(result.Has("datastore:.:" + name), name);
            var service = result.Fact("deployment:compose:.:" + name);
            Assert.Equal((FactKind.Deployment, "unknown", "service"), (service.Kind, service.Attr("kind"), service.Attr("role")));
        }
        Assert.Equal("postgres:16", result.Fact("deployment:compose:.:backup").Attr("image"));
        Assert.Equal("sh -c", result.Fact("deployment:compose:.:backup").Attr("command"));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "infrastructure-image-runs-a-tool", "compose.yaml", 7),
                new ExtractionNote(NoteKind.Undecided, "one-shot-service", "compose.yaml", 8),
                new ExtractionNote(NoteKind.Undecided, "infrastructure-image-runs-a-tool", "compose.yaml", 12),
                new ExtractionNote(NoteKind.Undecided, "one-shot-service", "compose.yaml", 15),
                new ExtractionNote(NoteKind.Undecided, "infrastructure-image-runs-a-tool", "compose.yaml", 19),
            ],
            result.Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_override_file_adds_to_the_fact_of_the_service_in_any_file_order(bool reversed)
    {
        (string, string)[] files =
        [
            ("docker-compose.yml", """
                services:
                  db:
                    image: postgres:16
                    ports:
                      - "5432:5432"
                  api:
                    build: ./api
                """),
            ("docker-compose.override.yml", """
                services:
                  db:
                    ports:
                      - "15432:5432"
                    environment:
                      POSTGRES_PASSWORD: hunter2
                  api:
                    image: acme/api:dev
                """),
            // The same service name in another directory is another compose project.
            ("ops/compose.yaml", "services:\n  db:\n    image: mysql:8\n"),
            // Two files of one project that give different images: no image wins.
            ("mixed/compose.yaml", "services:\n  db:\n    image: postgres:16\n"),
            ("mixed/compose.prod.yaml", "services:\n  db:\n    image: acme/pg-proxy:1\n"),
        ];

        var result = Repo.Extract(reversed ? [.. files.Reverse()] : files);

        // One fact: the image of the base file decides the kind, the override adds a port and a name.
        var db = result.Fact("datastore:.:db");
        Assert.Equal(("postgres", "postgres:16", "15432:5432,5432:5432", "POSTGRES_PASSWORD"),
            (db.Attr("kind"), db.Attr("image"), db.Attr("ports"), db.Attr("environment")));
        Assert.Equal(["docker-compose.override.yml:2-6", "docker-compose.yml:2-5"], db.Lines());
        Assert.False(result.Has("deployment:compose:.:db"));

        var api = result.Fact("deployment:compose:.:api");
        Assert.Equal(("unknown", "service", "acme/api:dev", "./api"), (api.Attr("kind"), api.Attr("role"), api.Attr("image"), api.Attr("build")));
        Assert.Equal(["docker-compose.override.yml:7-8", "docker-compose.yml:6-7"], api.Lines());

        Assert.Equal("mysql", result.Fact("datastore:ops:db").Attr("kind"));

        var mixed = result.Fact("deployment:compose:mixed:db");
        Assert.Equal(("unknown", "service"), (mixed.Attr("kind"), mixed.Attr("role")));
        Assert.Null(mixed.Attr("image"));
        Assert.Equal("acme/pg-proxy:1 @ mixed/compose.prod.yaml:2-3 | postgres:16 @ mixed/compose.yaml:2-3", mixed.Attr("conflict.image"));
        Assert.False(result.Has("datastore:mixed:db"));

        Assert.Equal(
            ["datastore:.:db", "datastore:ops:db", "deployment:compose:.:api", "deployment:compose:mixed:db"],
            result.Facts.Select(f => f.Key));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "conflicting-images", "mixed/compose.prod.yaml", 2),
                new ExtractionNote(NoteKind.Conflict, "attribute:image", "mixed/compose.prod.yaml", 2),
            ],
            result.Notes);
        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void A_compose_environment_item_is_a_name_only_when_it_is_a_valid_variable_name()
    {
        var result = Repo.Extract(("compose.yaml", """
            services:
              app:
                image: acme/app:1
                environment:
                  - GOOD_NAME=value-not-emitted
                  - ALSO_GOOD
                  - "not a name"
                  - 9LIVES=x
                  - "hunter2 secret=x"
                  - =orphan
              worker:
                image: acme/worker:1
                environment:
                  OK_NAME: x
                  "bad name": y
            """));

        Assert.Equal("ALSO_GOOD,GOOD_NAME", result.Fact("deployment:compose:.:app").Attr("environment"));
        Assert.Equal("OK_NAME", result.Fact("deployment:compose:.:worker").Attr("environment"));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "invalid-environment-name", "compose.yaml", 7),
                new ExtractionNote(NoteKind.Undecided, "invalid-environment-name", "compose.yaml", 8),
                new ExtractionNote(NoteKind.Undecided, "invalid-environment-name", "compose.yaml", 9),
                new ExtractionNote(NoteKind.Undecided, "invalid-environment-name", "compose.yaml", 10),
                new ExtractionNote(NoteKind.Undecided, "invalid-environment-name", "compose.yaml", 15),
            ],
            result.Notes);
        var json = ExtractionJson.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("not a name", json);
        Assert.DoesNotContain("value-not-emitted", json);
    }

    [Fact]
    public void An_image_reference_with_user_information_is_dropped_and_one_with_a_variable_is_noted()
    {
        var result = Repo.Extract(
            ("compose.yaml", """
                services:
                  a:
                    image: https://user:hunter2@registry.example.com/img:1
                  b:
                    image: ${REGISTRY}/app:${TAG}
                  c:
                    image: deploy@registry.example.com/img
                    build: https://user:hunter2@git.example.com/repo.git
                """),
            ("Dockerfile", "ARG BASE=hunter2\nFROM ${BASE} AS build\nFROM user:hunter2@registry.example.com/img\nEXPOSE 80\n"));

        Assert.Null(result.Fact("deployment:compose:.:a").Attr("image"));
        Assert.Equal("${REGISTRY}/app:${TAG}", result.Fact("deployment:compose:.:b").Attr("image"));
        var c = result.Fact("deployment:compose:.:c");
        Assert.Equal((null, null), (c.Attr("image"), c.Attr("build")));

        var dockerfile = result.Fact("deployment:dockerfile:Dockerfile");
        Assert.Null(dockerfile.Attr("baseImage"));
        Assert.Equal("${BASE}", dockerfile.Attr("stageImages"));
        Assert.Equal("80", dockerfile.Attr("ports"));

        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Undecided, "image-variable-not-resolved", "Dockerfile", 2),
                new ExtractionNote(NoteKind.Redacted, "image-reference-not-safe", "Dockerfile", 3),
                new ExtractionNote(NoteKind.Redacted, "image-reference-not-safe", "compose.yaml", 3),
                new ExtractionNote(NoteKind.Undecided, "image-variable-not-resolved", "compose.yaml", 5),
                new ExtractionNote(NoteKind.Redacted, "image-reference-not-safe", "compose.yaml", 7),
                new ExtractionNote(NoteKind.Undecided, "build-context-not-a-path", "compose.yaml", 8),
            ],
            result.Notes);
        var json = ExtractionJson.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("deploy@", json);
    }

    // ---- Dockerfile --------------------------------------------------------------------

    [Fact]
    public void A_dockerfile_gives_the_base_image_of_the_last_stage_and_what_that_stage_exposes()
    {
        var result = Repo.Extract(("build/api.Dockerfile", """
            # syntax=docker/dockerfile:1
            ARG VERSION=1
            FROM --platform=$BUILDPLATFORM node:22 AS deps
            EXPOSE 9229
            RUN npm ci \
                && npm run build

            FROM gcr.io/distroless/nodejs22 AS base
            FROM base AS final
            ENV API_KEY=not-in-any-fact
            LABEL secret=not-in-any-fact
            EXPOSE 3000 3001/udp
            # EXPOSE 4000
            ENTRYPOINT ["node", \
                        "server.js"]
            CMD ["--port", "3000"]
            """));

        var image = result.Fact("deployment:dockerfile:build/api.Dockerfile");
        Assert.Equal(FactKind.Deployment, image.Kind);
        Assert.Equal("build/api.Dockerfile", image.Name);
        Assert.Equal(("dockerfile", "gcr.io/distroless/nodejs22"), (image.Attr("source"), image.Attr("baseImage")));
        Assert.Equal("gcr.io/distroless/nodejs22,node:22", image.Attr("stageImages"));
        // The debug port belongs to a build stage and is not in the image.
        Assert.Equal("3000,3001/udp", image.Attr("ports"));
        // The executable, and no argument: "server.js" is a value.
        Assert.Equal("node", image.Attr("entrypoint"));
        // With an entrypoint the command is its argument list: the flag name, and not the port.
        Assert.Equal("--port", image.Attr("command"));
        Assert.Equal(
            [
                "build/api.Dockerfile:3-3", "build/api.Dockerfile:8-8", "build/api.Dockerfile:9-9",
                "build/api.Dockerfile:12-12", "build/api.Dockerfile:14-15", "build/api.Dockerfile:16-16",
            ],
            image.Lines());
        Assert.Equal([("docker.dockerfile", 2)], Rules(image));
        Assert.Empty(result.Notes);
        Assert.DoesNotContain("not-in-any-fact", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void A_dockerfile_command_keeps_the_executable_and_the_flag_names_only()
    {
        var result = Repo.Extract(
            ("a/Dockerfile", "FROM redis:7\nCMD [\"redis-server\", \"--requirepass\", \"hunter2\"]\n"),
            ("b/Dockerfile", "FROM alpine\nENTRYPOINT ./app --password hunter2 --api-key abc123 --mode=fast\n"),
            ("c/Dockerfile", """
                FROM alpine
                ENV TOKEN=hunter2
                ARG PASS=hunter2
                RUN echo hunter2 > /x
                ENTRYPOINT ["/bin/app"]
                CMD DB_PASSWORD=hunter2 serve --dsn=postgres://u:hunter2@db/x -p hunter2
                """));

        var json = ExtractionJson.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("abc123", json);
        // The JSON-array form.
        Assert.Equal("redis-server --requirepass", result.Fact("deployment:dockerfile:a/Dockerfile").Attr("command"));
        // The shell form, with values after a space and after '='.
        Assert.Equal("./app --password --api-key --mode", result.Fact("deployment:dockerfile:b/Dockerfile").Attr("entrypoint"));
        var c = result.Fact("deployment:dockerfile:c/Dockerfile");
        Assert.Equal(("/bin/app", "--dsn -p"), (c.Attr("entrypoint"), c.Attr("command")));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void A_heredoc_body_is_never_read_as_instructions()
    {
        var result = Repo.Extract(("Dockerfile",
            "FROM alpine:3.20 AS build\n" +          // 1
            "RUN <<EOT\n" +                          // 2
            "FROM evil AS fake\n" +                  // 3
            "EXPOSE 1111\n" +                        // 4
            "CMD [\"hunter2\"]\n" +                  // 5
            "EOT\n" +                                // 6
            "RUN <<-'ONE' && cat <<\"TWO\"\n" +      // 7
            "\tFROM evil2\n" +                       // 8
            "\tONE\n" +                              // 9: "<<-" takes the tabs off the delimiter line
            "ENTRYPOINT [\"nope\"]\n" +              // 10: the body of the second heredoc
            "TWO\n" +                                // 11
            "COPY <<EOF /etc/app.conf\n" +           // 12
            "EXPOSE 2222\n" +                        // 13
            "EOF\n" +                                // 14
            "EXPOSE 8080\n" +                        // 15
            "CMD [\"app\", \"--serve\"]\n"));        // 16

        var image = result.Fact("deployment:dockerfile:Dockerfile");
        Assert.Equal(("alpine:3.20", "alpine:3.20", "8080"), (image.Attr("baseImage"), image.Attr("stageImages"), image.Attr("ports")));
        Assert.Equal("app --serve", image.Attr("command"));
        Assert.Null(image.Attr("entrypoint"));
        Assert.Equal(["Dockerfile:1-1", "Dockerfile:15-15", "Dockerfile:16-16"], image.Lines());
        Assert.Empty(result.Notes);
        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));

        // A heredoc that never ends makes the rest of the file its body: nothing is said about the image.
        var open = Repo.Extract(("Dockerfile", "FROM alpine\nRUN <<EOT\nFROM evil\nCMD [\"x\"]\n"));
        Assert.Empty(open.Facts);
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "unterminated-heredoc", "Dockerfile", 2)], open.Notes);
    }

    [Fact]
    public void An_entrypoint_resets_the_command_that_a_stage_inherits()
    {
        var result = Repo.Extract(
            ("inherited/Dockerfile", """
                FROM node:22 AS base
                CMD ["node", "server.js"]
                EXPOSE 3000
                FROM base AS final
                ENTRYPOINT ["/tini", "--"]
                """),
            // In one stage Docker keeps a command that is set before the entrypoint.
            ("same/Dockerfile", "FROM alpine\nCMD [\"--help\"]\nENTRYPOINT [\"app\"]\n"),
            // A stage that sets only a command keeps the entrypoint of its parent.
            ("child/Dockerfile", "FROM alpine AS base\nENTRYPOINT [\"app\"]\nCMD [\"--a\"]\nFROM base\nCMD [\"--b\"]\n"));

        var inherited = result.Fact("deployment:dockerfile:inherited/Dockerfile");
        Assert.Equal(("node:22", "/tini", "3000"), (inherited.Attr("baseImage"), inherited.Attr("entrypoint"), inherited.Attr("ports")));
        Assert.Null(inherited.Attr("command"));
        Assert.Equal(
            ["inherited/Dockerfile:1-1", "inherited/Dockerfile:3-3", "inherited/Dockerfile:4-4", "inherited/Dockerfile:5-5"],
            inherited.Lines());

        var same = result.Fact("deployment:dockerfile:same/Dockerfile");
        Assert.Equal(("app", "--help"), (same.Attr("entrypoint"), same.Attr("command")));

        var child = result.Fact("deployment:dockerfile:child/Dockerfile");
        Assert.Equal(("app", "--b"), (child.Attr("entrypoint"), child.Attr("command")));
        Assert.Equal(["child/Dockerfile:1-1", "child/Dockerfile:2-2", "child/Dockerfile:4-4", "child/Dockerfile:5-5"], child.Lines());
    }

    // ---- Kubernetes --------------------------------------------------------------------

    [Fact]
    public void Kubernetes_workloads_are_read_and_other_kinds_are_not()
    {
        var result = Repo.Extract(("k8s/stock.yml", """
            apiVersion: apps/v1
            kind: StatefulSet
            metadata:
              name: stock-db
            spec:
              replicas: 3
              template:
                spec:
                  containers:
                    - name: postgres
                      image: postgres:16
                      ports:
                        - containerPort: 5432
                    - name: exporter
                      image: quay.io/prometheuscommunity/postgres-exporter:v0.15.0
                      ports:
                        - containerPort: 9187

            ---
            # A ConfigMap is not a deployment descriptor.
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: stock-config
            data:
              LOG_LEVEL: debug
            ---
            apiVersion: apps/v1
            kind: Deployment
            spec:
              replicas: 1
            """));

        var set = Assert.Single(result.Facts);
        // The file, the kind, the namespace ("default" when the manifest names none) and the name.
        Assert.Equal("deployment:k8s:k8s/stock.yml#statefulset/default/stock-db", set.Key);
        Assert.Equal(
            ("StatefulSet", "3", "postgres,exporter", "postgres:16,quay.io/prometheuscommunity/postgres-exporter:v0.15.0", "5432,9187"),
            (set.Attr("kind"), set.Attr("replicas"), set.Attr("containers"), set.Attr("images"), set.Attr("ports")));
        // The manifest does not say "default": the attribute is not made up.
        Assert.Null(set.Attr("namespace"));
        // The blank line and the separator after the document are not part of it.
        Assert.Equal(["k8s/stock.yml:1-17"], set.Lines());
        Assert.Equal([("k8s.workload", 2)], Rules(set));
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "unnamed-manifest", "k8s/stock.yml", 28)], result.Notes);
    }

    [Fact]
    public void Kubernetes_commands_keep_flag_names_and_services_and_ingresses_are_read()
    {
        var result = Repo.Extract(("k8s/shop.yaml", """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: api
              namespace: shop
            spec:
              replicas: 2
              template:
                spec:
                  containers:
                    - name: api
                      image: ghcr.io/acme/api:1.2.3
                      command: ["/app/api", "--db-password", "hunter2-a", "--listen=:8080"]
                      args: ["serve", "--token", "hunter2-b", "--key=hunter2-c"]
                      env:
                        - name: DB_PASSWORD
                          value: hunter2-d
                      ports:
                        - containerPort: 8080
                    - name: sidecar
                      image: https://user:hunter2-e@registry.example.com/proxy:1
                      args:
                        - --upstream
                        - hunter2-f
            ---
            apiVersion: v1
            kind: Service
            metadata:
              name: api
              namespace: shop
            spec:
              type: ClusterIP
              selector:
                app: api
                token: hunter2g
              ports:
                - port: 80
                  targetPort: 8080
                - port: 443
                  targetPort: https
            ---
            apiVersion: networking.k8s.io/v1
            kind: Ingress
            metadata:
              name: web
              annotations:
                example.com/auth: hunter2-h
            spec:
              rules:
                - host: shop.example.com
                  http:
                    paths:
                      - path: /api
                        backend:
                          service:
                            name: api
                            port:
                              number: 80
                      - backend:
                          service:
                            name: web
                            port:
                              name: http
            """));

        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));

        var workload = result.Fact("deployment:k8s:k8s/shop.yaml#deployment/shop/api");
        Assert.Equal(("kubernetes", "Deployment", "api", "shop", "2"),
            (workload.Attr("source"), workload.Attr("kind"), workload.Attr("name"), workload.Attr("namespace"), workload.Attr("replicas")));
        Assert.Equal(("api,sidecar", "ghcr.io/acme/api:1.2.3", "8080"), (workload.Attr("containers"), workload.Attr("images"), workload.Attr("ports")));
        // "command" has the executable; "args" is an argument list, so "serve" is a value like any other.
        Assert.Equal("/app/api --db-password --listen", workload.Attr("command.api"));
        Assert.Equal("--token --key", workload.Attr("args.api"));
        Assert.Equal("--upstream", workload.Attr("args.sidecar"));
        Assert.Null(workload.Attr("command.sidecar"));
        Assert.Equal(["k8s/shop.yaml:1-24"], workload.Lines());

        var service = result.Fact("deployment:k8s:k8s/shop.yaml#service/shop/api");
        Assert.Equal(("Service", "ClusterIP", "80->8080,443->https", "app=api"),
            (service.Attr("kind"), service.Attr("type"), service.Attr("ports"), service.Attr("selector")));
        Assert.Equal(["k8s/shop.yaml:26-40"], service.Lines());
        Assert.Equal([("k8s.service", 2)], Rules(service));

        var ingress = result.Fact("deployment:k8s:k8s/shop.yaml#ingress/default/web");
        Assert.Equal("shop.example.com/api->api:80,shop.example.com/->web:http", ingress.Attr("rules"));
        Assert.Null(ingress.Attr("namespace"));
        Assert.Equal(["k8s/shop.yaml:42-63"], ingress.Lines());
        Assert.Equal([("k8s.ingress", 2)], Rules(ingress));

        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Redacted, "image-reference-not-safe", "k8s/shop.yaml", 21),
                new ExtractionNote(NoteKind.Redacted, "selector-not-emitted", "k8s/shop.yaml", 35),
            ],
            result.Notes);
    }

    [Fact]
    public void The_items_of_a_kind_List_are_read_as_manifests_and_a_Secret_among_them_refuses_the_file()
    {
        var result = Repo.Extract(
            ("k8s/list.yaml", """
                apiVersion: v1
                kind: List
                items:
                  - apiVersion: v1
                    kind: Service
                    metadata:
                      name: a
                  - apiVersion: apps/v1
                    kind: Deployment
                    metadata:
                      name: b
                      namespace: jobs
                """),
            ("k8s/bundle.yaml", """
                apiVersion: v1
                kind: List
                items:
                  - apiVersion: v1
                    kind: Service
                    metadata:
                      name: hidden
                  - apiVersion: v1
                    kind: Secret
                    metadata:
                      name: db
                    stringData:
                      password: do-not-read
                """));

        Assert.Equal(
            ["deployment:k8s:k8s/list.yaml#deployment/jobs/b", "deployment:k8s:k8s/list.yaml#service/default/a"],
            result.Facts.Select(f => f.Key));
        Assert.Equal(["k8s/list.yaml:4-7"], result.Fact("deployment:k8s:k8s/list.yaml#service/default/a").Lines());
        Assert.Equal(["k8s/list.yaml:8-12"], result.Fact("deployment:k8s:k8s/list.yaml#deployment/jobs/b").Lines());
        Assert.Equal([new ExtractionNote(NoteKind.Skipped, "secret-file", "k8s/bundle.yaml", 0)], result.Notes);
        Assert.DoesNotContain("do-not-read", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void A_manifest_with_a_Secret_in_any_document_is_refused_whole_even_when_it_does_not_parse()
    {
        const string Deployment = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: api\n";
        var result = Repo.Extract(
            ("k8s/all.yaml", Deployment + "---\napiVersion: v1\nkind: Secret\nmetadata:\n  name: db\nstringData:\n  password: do-not-read\n"),
            ("k8s/quoted.yaml", "apiVersion: v1\nkind: \"Secret\"\nstringData: {password: do-not-read}\n"),
            ("k8s/broken.yaml", "kind: Secret\nstringData:\n  password: [do-not-read\n"),
            ("k8s/manifest.json", """{ "apiVersion": "v1", "kind": "Secret", "stringData": { "password": "do-not-read" } }"""),
            ("k8s/api.yaml", Deployment));

        Assert.Equal(["deployment:k8s:k8s/api.yaml#deployment/default/api"], result.Facts.Select(f => f.Key));
        Assert.Equal(["k8s/api.yaml:1-4"], result.Facts[0].Lines());
        Assert.Equal(
            ["k8s/all.yaml", "k8s/broken.yaml", "k8s/manifest.json", "k8s/quoted.yaml"],
            result.Notes.Select(n => n.Path));
        Assert.All(result.Notes, n => Assert.Equal((NoteKind.Skipped, "secret-file"), (n.Kind, n.Reason)));
        Assert.DoesNotContain("do-not-read", ExtractionJson.Serialize(result));
    }

    // ---- workflows ---------------------------------------------------------------------

    [Fact]
    public void A_workflow_gives_names_only_and_says_which_jobs_build_or_deploy()
    {
        var result = Repo.Extract((".github/workflows/release.yaml", """
            name: Release
            on: [push, workflow_dispatch]
            env:
              API_TOKEN: not-in-any-fact
            jobs:
              lint:
                runs-on: ubuntu-latest
                steps:
                  - run: npm run lint
              container:
                runs-on: ubuntu-latest
                steps:
                  - run: |
                      docker build -t app .
                      docker push app
              ship:
                name: Roll out to production
                runs-on: ubuntu-latest
                steps:
                  - uses: azure/k8s-deploy@v5
                    with:
                      manifests: k8s/
            """));

        var workflow = Assert.Single(result.Facts);
        Assert.Equal("deployment:workflow:.github/workflows/release.yaml", workflow.Key);
        Assert.Equal("Release", workflow.Name);
        Assert.Equal(("github-actions", "Release"), (workflow.Attr("source"), workflow.Attr("name")));
        Assert.Equal(
            ("push,workflow_dispatch", "lint,container,ship", "container", "ship", "lint"),
            (workflow.Attr("triggers"), workflow.Attr("jobs"), workflow.Attr("buildJobs"), workflow.Attr("deployJobs"), workflow.Attr("otherJobs")));
        Assert.Equal(
            [".github/workflows/release.yaml:1-1", ".github/workflows/release.yaml:10-15", ".github/workflows/release.yaml:16-22"],
            workflow.Lines());
        Assert.Equal([("github.workflow", 2)], Rules(workflow));
        Assert.Empty(result.Notes);
        Assert.DoesNotContain("not-in-any-fact", ExtractionJson.Serialize(result));

        // The same file outside .github/workflows is not a workflow.
        Assert.Empty(Repo.Extract(("docs/release.yaml", "name: Release\njobs:\n  deploy:\n    steps: []\n")).Facts);
    }

    [Fact]
    public void A_job_is_classified_by_exact_actions_and_commands_and_never_by_its_name()
    {
        var result = Repo.Extract((".github/workflows/ci.yml", """
            name: CI
            on:
              push:
                branches: [main]
            jobs:
              build-docs:
                steps:
                  - run: echo "docker build is not run here"
              deploy-preview-cleanup:
                name: Deploy release rollout
                steps:
                  - uses: actions/checkout@v4
                  - uses: helm/kind-action@v1
                  - run: echo kubectl apply
              image:
                steps:
                  - uses: docker/build-push-action@v6
                    with:
                      secrets: hunter2-a
              both:
                steps:
                  - env:
                      TOKEN: hunter2-b
                    run: |
                      set -e
                      DOCKER_BUILDKIT=1 docker buildx build -t app --secret hunter2-c .
                      helm upgrade --install app ./chart --set password=hunter2-d
              fork:
                uses: acme/workflows/.github/workflows/deploy.yml@main
            """));

        var workflow = Assert.Single(result.Facts);
        Assert.Equal("push", workflow.Attr("triggers"));
        Assert.Equal("build-docs,deploy-preview-cleanup,image,both,fork", workflow.Attr("jobs"));
        // A job with both kinds of steps is in both lists. Every other job is in exactly one list.
        Assert.Equal(("image,both", "both", "build-docs,deploy-preview-cleanup,fork"),
            (workflow.Attr("buildJobs"), workflow.Attr("deployJobs"), workflow.Attr("otherJobs")));
        Assert.Equal(
            [".github/workflows/ci.yml:1-1", ".github/workflows/ci.yml:15-19", ".github/workflows/ci.yml:20-27"],
            workflow.Lines());
        Assert.Empty(result.Notes);
        var json = ExtractionJson.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        // No text of a "run" step is in the output.
        Assert.DoesNotContain("buildx", json);
        Assert.DoesNotContain("echo", json);
    }

    [Fact]
    public void A_name_that_looks_like_a_credential_is_left_out_and_the_fact_stays()
    {
        var result = Repo.Extract(
            (".github/workflows/deploy.yml", "name: Deploy password=hunter2\non: push\njobs:\n  ship:\n    steps:\n      - run: kubectl apply -f k8s/ --token=hunter2\n"),
            ("openapi.yaml", "openapi: 3.0.0\ninfo:\n  title: \"Orders token: hunter2\"\npaths: {}\n"));

        // The fact is named by its file, and it still says what the workflow does.
        var workflow = result.Fact("deployment:workflow:.github/workflows/deploy.yml");
        Assert.Equal(("deploy.yml", null, "ship"), (workflow.Name, workflow.Attr("name"), workflow.Attr("deployJobs")));
        Assert.Equal([".github/workflows/deploy.yml:4-6"], workflow.Lines());
        var contract = result.Fact("contract:openapi.yaml");
        Assert.Equal(("openapi.yaml", null, "3.0.0"), (contract.Name, contract.Attr("title"), contract.Attr("specVersion")));
        Assert.Equal(
            [
                new ExtractionNote(NoteKind.Redacted, "name-not-emitted", ".github/workflows/deploy.yml", 1),
                new ExtractionNote(NoteKind.Redacted, "title-not-emitted", "openapi.yaml", 3),
            ],
            result.Notes);
        Assert.DoesNotContain("hunter2", ExtractionJson.Serialize(result));
    }

    [Fact]
    public void Evidence_ends_at_the_last_line_of_a_block_scalar_even_when_that_line_starts_with_a_hash()
    {
        var result = Repo.Extract((".github/workflows/ci.yml", """
            name: CI
            on: push
            jobs:
              image:
                steps:
                  - run: |
                      docker build .
                      # done

              # a comment between the jobs
              lint:
                steps:
                  - run: echo ok
            """));

        var workflow = Assert.Single(result.Facts);
        Assert.Equal(("image", "lint"), (workflow.Attr("buildJobs"), workflow.Attr("otherJobs")));
        // Line 8 is the last line of the script. Line 9 is blank and line 10 is a YAML comment.
        Assert.Equal([".github/workflows/ci.yml:1-1", ".github/workflows/ci.yml:4-8"], workflow.Lines());
    }

    // ---- bounded work ------------------------------------------------------------------

    [Fact]
    public void A_chain_of_thousands_of_stages_is_read_in_linear_work_and_gives_no_fact()
    {
        // 4 500 stages that each start from the one before, each with 13 ports of its own.
        var content = string.Concat(Enumerable.Range(0, 4500).Select(i => $"FROM s{i} AS s{i + 1}\nEXPOSE 1 2 3 4 5 6 7 8 9 10 11 12 13\n"));

        var (result, work) = Repo.ExtractBounded("Dockerfile", content);

        // More stages than the reader follows: the last stage is not known, so nothing is said.
        Assert.Empty(result.Facts);
        Assert.Equal([new ExtractionNote(NoteKind.LimitReached, "limit:dockerfile-stages", "Dockerfile", 513)], result.Notes);
        Assert.False(result.IsComplete);
        Assert.InRange(work, 1, 4L * content.Length);
    }

    [Fact]
    public void A_chain_of_stages_under_the_maximum_gives_the_ports_of_the_chain_cut_at_the_list_bound()
    {
        // 200 stages, three new ports in each: 600 ports along the chain.
        var content = string.Concat(Enumerable.Range(0, 200).Select(i =>
            $"FROM {(i == 0 ? "alpine:3" : $"s{i - 1}")} AS s{i}\nEXPOSE {3 * i + 1} {3 * i + 2} {3 * i + 3}\n"));

        var (result, _) = Repo.ExtractBounded("Dockerfile", content);

        var image = result.Fact("deployment:dockerfile:Dockerfile");
        Assert.Equal(("alpine:3", "alpine:3"), (image.Attr("baseImage"), image.Attr("stageImages")));
        var ports = image.Attr("ports")!.Split(',');
        Assert.Equal(Enumerable.Range(1, 200).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)), ports);
        Assert.Contains(new ExtractionNote(NoteKind.LimitReached, "limit:list-items", "Dockerfile", 1), result.Notes);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void One_instruction_continued_over_thousands_of_lines_is_read_in_linear_work()
    {
        var content = HostileDocuments.DockerfileContinuation();

        var (result, work) = Repo.ExtractBounded("Dockerfile", content);

        var image = result.Fact("deployment:dockerfile:Dockerfile");
        Assert.Equal(200, image.Attr("ports")!.Split(',').Length);
        Assert.StartsWith("1,2,3,4,", image.Attr("ports"));
        Assert.Contains("limit:list-items", result.Reasons());
        Assert.InRange(work, 1, 4L * content.Length);
    }

    [Fact]
    public void Thousands_of_heredocs_are_skipped_in_linear_work()
    {
        var content = HostileDocuments.DockerfileHeredocs();

        var (result, work) = Repo.ExtractBounded("Dockerfile", content);

        var image = result.Fact("deployment:dockerfile:Dockerfile");
        Assert.Equal(("scratch", "scratch", "80"), (image.Attr("baseImage"), image.Attr("stageImages"), image.Attr("ports")));
        Assert.Equal(2, image.Evidence.Count);
        Assert.Empty(result.Notes);
        Assert.InRange(work, 1, 4L * content.Length);
    }

    [Fact]
    public void Thousands_of_compose_services_are_read_in_bounded_work()
    {
        var (result, work) = Repo.ExtractBounded("docker-compose.yml", HostileDocuments.ComposeServices());

        Assert.True(work > 0);
        Assert.Equal(("postgres", "0:5432"), (result.Fact("datastore:.:s0").Attr("kind"), result.Fact("datastore:.:s0").Attr("ports")));
        Assert.Equal(2000, result.Facts.Count);
        Assert.Contains("limit:facts-per-file", result.Reasons());
    }

    [Fact]
    public void Thousands_of_workflow_jobs_and_steps_are_read_in_bounded_work()
    {
        var (jobs, jobWork) = Repo.ExtractBounded(".github/workflows/ci.yml", HostileDocuments.WorkflowJobs());

        var workflow = Assert.Single(jobs.Facts);
        Assert.True(jobWork > 0);
        Assert.Equal(200, workflow.Attr("jobs")!.Split(',').Length);
        Assert.Equal(200, workflow.Attr("buildJobs")!.Split(',').Length);
        Assert.Equal(200, workflow.Attr("deployJobs")!.Split(',').Length);
        Assert.Null(workflow.Attr("otherJobs"));
        Assert.InRange(workflow.Evidence.Count, 1, 200);
        Assert.Contains("limit:list-items", jobs.Reasons());
        Assert.False(jobs.IsComplete);

        var (steps, stepWork) = Repo.ExtractBounded(".github/workflows/ci.yml", HostileDocuments.WorkflowSteps());

        Assert.True(stepWork > 0);
        Assert.Equal(("big", null, "big"), (Assert.Single(steps.Facts).Attr("jobs"), steps.Facts[0].Attr("buildJobs"), steps.Facts[0].Attr("otherJobs")));
        Assert.True(steps.IsComplete);
    }

    [Fact]
    public void Thousands_of_kubernetes_documents_list_items_and_containers_are_read_in_bounded_work()
    {
        var (documents, documentWork) = Repo.ExtractBounded("k8s/all.yaml", HostileDocuments.KubernetesDocuments());

        Assert.True(documentWork > 0);
        Assert.Equal("80", documents.Fact("deployment:k8s:k8s/all.yaml#service/default/s0").Attr("ports"));
        Assert.Equal(2000, documents.Facts.Count);
        Assert.Contains("limit:facts-per-file", documents.Reasons());

        var (list, listWork) = Repo.ExtractBounded("k8s/list.yaml", HostileDocuments.KubernetesList());

        Assert.True(listWork > 0);
        Assert.True(list.Has("deployment:k8s:k8s/list.yaml#service/default/s1999"));
        Assert.Equal(2000, list.Facts.Count);

        var (containers, containerWork) = Repo.ExtractBounded("k8s/big.yaml", HostileDocuments.KubernetesContainers());

        Assert.True(containerWork > 0);
        var workload = Assert.Single(containers.Facts);
        Assert.Equal(200, workload.Attr("containers")!.Split(',').Length);
        Assert.Equal("app --port", workload.Attr("command.c0"));
        Assert.Equal("--x", workload.Attr("args.c0"));
        // Commands are kept for the first 32 containers only.
        Assert.Equal(32, workload.Attributes.Keys.Count(k => k.StartsWith("command.", StringComparison.Ordinal)));
        Assert.Contains("limit:containers", containers.Reasons());
        Assert.Contains("limit:list-items", containers.Reasons());
        Assert.False(containers.IsComplete);
    }
}

internal static class HostileDocuments
{
    /// <summary>One instruction that is continued over tens of thousands of lines, with a port on each.</summary>
    public static string DockerfileContinuation() =>
        HostileText.Fill("FROM scratch\nEXPOSE 1 \\\n", i => $" {i % 60000 + 2} \\\n", " 65000\n");

    /// <summary>Thousands of heredocs whose bodies look like instructions.</summary>
    public static string DockerfileHeredocs() =>
        HostileText.Fill("FROM scratch\n", _ => "RUN <<EOF\nFROM x AS y\nEXPOSE 9\nEOF\n", "EXPOSE 80\n");

    public static string ComposeServices() =>
        HostileText.Fill("services:\n", i => $"  s{i}: {{image: postgres, ports: ['{i % 60000}:5432']}}\n");

    public static string WorkflowJobs() =>
        HostileText.Fill("on: push\njobs:\n", i => $"  j{i}: {{steps: [{{run: docker build .}}, {{uses: azure/k8s-deploy@v5}}]}}\n");

    public static string WorkflowSteps() =>
        HostileText.Fill("on: push\njobs:\n  big:\n    steps:\n", i => $"      - run: echo {i}\n");

    public static string KubernetesDocuments() =>
        HostileText.Fill("", i => $"---\napiVersion: v1\nkind: Service\nmetadata: {{name: s{i}}}\nspec: {{ports: [{{port: 80}}]}}\n");

    public static string KubernetesContainers() =>
        HostileText.Fill(
            "apiVersion: apps/v1\nkind: Deployment\nmetadata: {name: big}\nspec:\n  template:\n    spec:\n      containers:\n",
            i => $"        - {{name: c{i}, image: 'img:{i}', command: [app, --port, '80'], args: [--x=1], ports: [{{containerPort: {i % 60000}}}]}}\n");

    public static string KubernetesList() =>
        HostileText.Fill("apiVersion: v1\nkind: List\nitems:\n", i => $"  - {{apiVersion: v1, kind: Service, metadata: {{name: s{i}}}}}\n");

    public static IEnumerable<(string Name, string Path, string Content)> All() =>
    [
        ("dockerfile-stages", "Dockerfile", string.Concat(Enumerable.Range(0, 4500).Select(i => $"FROM s{i} AS s{i + 1}\nEXPOSE 1 2 3 4 5 6 7 8 9 10 11 12 13\n"))),
        ("dockerfile-continuation", "Dockerfile", DockerfileContinuation()),
        ("dockerfile-heredocs", "Dockerfile", DockerfileHeredocs()),
        ("compose-services", "docker-compose.yml", ComposeServices()),
        ("workflow-jobs", ".github/workflows/ci.yml", WorkflowJobs()),
        ("workflow-steps", ".github/workflows/ci.yml", WorkflowSteps()),
        ("k8s-documents", "k8s/all.yaml", KubernetesDocuments()),
        ("k8s-containers", "k8s/big.yaml", KubernetesContainers()),
        ("k8s-list", "k8s/list.yaml", KubernetesList()),
    ];
}
