using Vellum.Modules.Import.Extraction;

namespace Vellum.Tests.Modules.Import;

/// <summary>Compose, Dockerfile, Kubernetes and workflow rules, in the forms the Orders fixture does not use.</summary>
public class DeploymentExtractorTests
{
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
            """));

        var cache = result.Fact("datastore:cache");
        Assert.Equal(FactKind.DataStore, cache.Kind);
        Assert.Equal("redis", cache.Attr("kind"));
        Assert.Equal("ALLOW_EMPTY_PASSWORD,REDIS_PASSWORD", cache.Attr("environment"));
        // An image digest is public by design and stays.
        Assert.StartsWith("docker.io/bitnami/redis:7.2@sha256:", cache.Attr("image"));
        Assert.Equal(["deploy/compose.yaml:2-6"], cache.Lines());

        var events = result.Fact("deployment:compose:events");
        Assert.Equal(FactKind.Deployment, events.Kind);
        Assert.Equal(("kafka", "broker", "19092:9092,9093"), (events.Attr("kind"), events.Attr("role"), events.Attr("ports")));

        var search = result.Fact("deployment:compose:search");
        Assert.Equal(("unknown", "service", "cache"), (search.Attr("kind"), search.Attr("role"), search.Attr("dependsOn")));

        var web = result.Fact("deployment:compose:web");
        Assert.Equal(("./web", "API_URL,SESSION_KEY", "unknown"), (web.Attr("build"), web.Attr("environment"), web.Attr("kind")));
        Assert.Equal(["deploy/compose.yaml:18-22"], web.Lines());

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
            EXPOSE 3000 3001/udp
            # EXPOSE 4000
            ENTRYPOINT ["node", \
                        "server.js"]
            CMD ["--port", "3000"]
            """));

        var image = result.Fact("deployment:dockerfile:build/api.Dockerfile");
        Assert.Equal("gcr.io/distroless/nodejs22", image.Attr("baseImage"));
        Assert.Equal("gcr.io/distroless/nodejs22,node:22", image.Attr("stageImages"));
        // The debug port belongs to a build stage and is not in the image.
        Assert.Equal("3000,3001/udp", image.Attr("ports"));
        Assert.Equal("[\"node\", \"server.js\"]", image.Attr("entrypoint"));
        Assert.Equal("[\"--port\", \"3000\"]", image.Attr("command"));
        Assert.Equal(
            [
                "build/api.Dockerfile:3-3", "build/api.Dockerfile:8-8", "build/api.Dockerfile:9-9",
                "build/api.Dockerfile:11-11", "build/api.Dockerfile:13-14", "build/api.Dockerfile:15-15",
            ],
            image.Lines());
        Assert.DoesNotContain("not-in-any-fact", ExtractionJson.Serialize(result));
    }

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
        Assert.Equal("deployment:k8s:statefulset/stock-db", set.Key);
        Assert.Equal(
            ("StatefulSet", "3", "postgres,exporter", "postgres:16,quay.io/prometheuscommunity/postgres-exporter:v0.15.0", "5432,9187"),
            (set.Attr("kind"), set.Attr("replicas"), set.Attr("containers"), set.Attr("images"), set.Attr("ports")));
        // The blank line and the separator after the document are not part of it.
        Assert.Equal(["k8s/stock.yml:1-17"], set.Lines());
        Assert.Equal([new ExtractionNote(NoteKind.Undecided, "unnamed-manifest", "k8s/stock.yml", 28)], result.Notes);
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

        Assert.Equal(["deployment:k8s:deployment/api"], result.Facts.Select(f => f.Key));
        Assert.Equal(["k8s/api.yaml:1-4"], result.Facts[0].Lines());
        Assert.Equal(
            ["k8s/all.yaml", "k8s/broken.yaml", "k8s/manifest.json", "k8s/quoted.yaml"],
            result.Notes.Select(n => n.Path));
        Assert.All(result.Notes, n => Assert.Equal((NoteKind.Skipped, "secret-file"), (n.Kind, n.Reason)));
        Assert.DoesNotContain("do-not-read", ExtractionJson.Serialize(result));
    }

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
        Assert.Equal(
            ("push,workflow_dispatch", "lint,container,ship", "container", "ship"),
            (workflow.Attr("triggers"), workflow.Attr("jobs"), workflow.Attr("buildJobs"), workflow.Attr("deployJobs")));
        Assert.Equal(
            [".github/workflows/release.yaml:1-1", ".github/workflows/release.yaml:10-15", ".github/workflows/release.yaml:16-22"],
            workflow.Lines());
        Assert.DoesNotContain("not-in-any-fact", ExtractionJson.Serialize(result));

        // The same file outside .github/workflows is not a workflow.
        Assert.Empty(Repo.Extract(("docs/release.yaml", "name: Release\njobs:\n  deploy:\n    steps: []\n")).Facts);
    }
}
