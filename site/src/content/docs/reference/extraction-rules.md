---
title: Extraction rules
description: What a repository scan reads, what each rule recognises and what it does not.
---

This page lists what a scan extracts. Every rule has an identifier and a version. A fact records the rule that produced it.

A rule produces a fact only when the source text establishes it. When a rule cannot be sure, it produces nothing and adds a note to the scan.

## What a scan reads

A scan reads files with these names: `*.cs`, `*.csproj`, `*.go`, `go.mod`, `*.json`, `*.yaml`, `*.yml`, and Dockerfiles.

A scan never reads:

- Binary files, minified files and lock files.
- `node_modules`, `vendor`, `bin`, `obj`, `dist` and `.git`.
- Files that look like a secret store: `.env` files, `*.pem`, `*.key`, `*.pfx`, SSH keys, `secrets.*`, `*.tfvars`, and Kubernetes Secret manifests.

A fact never holds a value from the source: no environment value, no command argument, no default or example from a schema, and no user name or password from a URL. It holds names, types and structure.

## Rules

| Area | Recognises | Does not recognise |
|---|---|---|
| OpenAPI 3.0 and 3.1 | Operations, component schemas, servers, whether security is required, optional or absent. | Swagger 2. References to other files. Callbacks and webhooks. Two documents in one file. |
| AsyncAPI 2 and 3 | Channels, messages, who sends and who receives, AMQP and Kafka bindings. | Traits. References to other files. Other bindings. |
| Docker Compose | Services, images, ports, dependencies, names of environment variables. Known images as data stores and brokers. | `extends`, `include`, profiles, variable substitution. |
| .NET projects | Projects as services, package references. | `Directory.Build.props`, central package versions. |
| .NET minimal API | `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete` and `MapGroup` with a fixed prefix on an application or route builder. | `MapMethods`, `MapFallback`. A route in an extension method. A route that is not one string. |
| .NET controllers | `[Route]` with `[HttpGet]` and the other verb attributes, with base and partial classes when every part is in the scan. | `[AcceptVerbs]`, areas, conventional routing. |
| MassTransit | Consumers, receive endpoints, publish and send on a bus type, the Entity Framework outbox. | Sagas, convention-registered consumers. Open generic types. |
| Hand-written outbox | An outbox table and a hosted service in the same project that reads it and publishes. | A dispatcher in another project. CAP, NServiceBus, Wolverine. |
| EF Core | A `DbContext` and its `DbSet` properties. | Entities mapped only in `OnModelCreating`. Dapper and raw SQL. |
| Go modules | The module as a service, its requirements. | `go.work`, `replace`. |
| Go routes | `net/http`, chi, gin and echo routes on a router declared in the same function. | A router held in a struct field, returned from a function or passed in. |
| Go brokers | AMQP exchanges, queues, bindings, publish and consume. Kafka topics. NATS subjects. A struct that a consumer decodes a delivery into. | JetStream. A decode in a helper function. |
| Dockerfile | Base image, ports, the executable and flag names of the entrypoint. | `ARG`, `ENV`, substitution. |
| Kubernetes | Deployment, StatefulSet, DaemonSet, Service, Ingress: names, images, ports, replicas. | Helm templates, Kustomize, other kinds. |
| GitHub Actions | The workflow and its jobs, with build and deploy jobs named by known actions and commands. | Reusable workflows, matrix expansion. |

Code inside `#if false` produces nothing. Code inside `#if SYMBOL` produces facts with the condition recorded.

## Limits of one scan

| Limit | Default |
|---|---|
| Files | 2,000 |
| Size of one file | 256 KB |
| Total size | 20 MB |
| Facts | 20,000 |

A scan that reaches a limit is partial. It keeps what it found and says so.

## When the rules change

A new version of Vellum can change a rule. The next scan then matches the facts in the model to the facts it finds before it compares them. It proposes no removal because of a rule change. When it cannot match a fact exactly, the review asks you to choose.
