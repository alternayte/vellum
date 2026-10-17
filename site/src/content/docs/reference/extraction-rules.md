---
title: Extraction rules
description: What a repository scan reads, what each rule recognises and what it does not.
---

This page lists what a scan extracts. Every rule has an identifier and a version. A fact records the rule that produced it.

A rule produces a fact only when the source text establishes it. When a rule cannot be sure, it produces nothing and adds a note to the scan.

## What a scan reads

A scan reads files with these names: `*.cs`, `*.csproj`, `*.go`, `go.mod`, `*.json`, `*.yaml`, `*.yml`, and Dockerfiles (`Dockerfile`, `Dockerfile.*` and `*.Dockerfile`).

A scan never reads:

- Files with a binary extension, such as images, archives, compiled files, fonts and database files.
- Minified files (`*.min.*`) and source maps (`*.map`).
- Lock files: `package-lock.json`, `packages.lock.json`, `pnpm-lock.yaml`, `npm-shrinkwrap.json`, `go.sum`, `bun.lockb` and `*.lock`.
- The directories `node_modules`, `vendor`, `bin`, `obj`, `dist` and `.git`.
- Go test files (`*_test.go`).
- Files that look like a secret store. Vellum decides this from the path alone, before it reads the content:
  - The directories `.ssh`, `.aws`, `.gnupg`, `.azure` and `.kube`.
  - Names that start with `.env`, `id_rsa`, `id_dsa`, `id_ecdsa`, `id_ed25519` or `secrets.`.
  - Names that end with `.pem`, `.key`, `.pfx`, `.p12`, `.jks`, `.keystore`, `.kdbx`, `.tfvars`, `.tfvars.json` or `.tfstate`.
  - The names `.npmrc`, `.pypirc`, `.netrc`, `.htpasswd`, `.dockercfg`, `kubeconfig`, `credentials` and `credentials.json`, `.yaml`, `.yml` or `.xml`.
  - A `.yaml`, `.yml` or `.json` file whose name contains `secret`.
- A file in which a document or a list item is a Kubernetes Secret, whatever its name.

A path is also refused when it has more than 1,024 characters, starts with `/`, or has a `\`, a control character or a `.` or `..` part.

A fact never holds a value from the source: no environment value, no command argument, no default or example from a schema, and no user name or password from a URL. It holds names, types and structure.

## Rules

| Area | Recognises | Does not recognise |
|---|---|---|
| OpenAPI 3.0 and 3.1 | Operations, component schemas, servers, whether security is required, optional or absent. | Swagger 2. References to other files. A request body or a response behind `$ref`. Path-level parameters, callbacks, webhooks and links. The security schemes. Two documents in one file. |
| AsyncAPI 2 and 3 | Channels, messages, who sends and who receives, AMQP and Kafka bindings of channels and operations. | Traits. References to other files. A channel or an operation behind `$ref`. Server and message bindings. Other bindings. Correlation IDs, headers and reply operations. Server variables and security. Two documents in one file. |
| Docker Compose | Services, images, ports, dependencies, names of environment variables. Known images as data stores and brokers. | `extends`, `include`, profiles, merge keys, variable substitution, networks, volumes and labels. |
| .NET projects | A project as a service when its SDK is Web or Worker, or its output type is `Exe` or `WinExe`. Package references and project references. | `Directory.Build.props`, imports, `Condition` attributes, central package versions, transitive packages. |
| .NET minimal API | `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete` and `MapGroup` with a fixed prefix on an application or route builder. | `MapMethods`, `MapFallback`. A route in an extension method. A route that is not one string. |
| .NET controllers | `[Route]` with `[HttpGet]` and the other verb attributes, with base and partial classes when every part is in the scan. | `[AcceptVerbs]`, areas, conventional routing. |
| MassTransit | Consumers, receive endpoints, publish and send on a bus type, entity names set with `SetEntityName`, the Entity Framework outbox and the in-memory outbox. | Sagas, convention-registered consumers. Open generic types. `PublishBatch` and scheduled messages. |
| .NET message contracts | The fields of a message type that a consumer, a publish, a send or an entity name refers to: the positional parameters of a record and the public properties, as name and type. | Inherited members, expression-bodied properties, fields. A type that is declared outside the scan. |
| Hand-written outbox | An outbox table and a hosted service in the same project that reads it and publishes. | A dispatcher in another project. CAP, NServiceBus, Wolverine. |
| EF Core | A `DbContext` and its `DbSet` properties. | Entities mapped only in `OnModelCreating`. Dapper and raw SQL. |
| Go modules | The module as a service, its requirements. | `go.work`, `replace`, `exclude`. Several commands in one module give one service. |
| Go routes | `net/http`, chi, gin and echo routes on a router declared in the same function. | A router held in a struct field, returned from a function or passed in. |
| Go brokers | AMQP exchanges, queues, bindings, publish and consume. Kafka topics. NATS subjects. A struct that a consumer decodes a delivery into with `json.Unmarshal`. | JetStream. An AMQP channel that is a struct field. A decode in a helper function or with another decoder. A struct with an embedded field. |
| Go SQL | That a module opens a database connection with `database/sql`, sqlx, pgx or pgxpool. The name of the variable when the connection string is `os.Getenv("NAME")`. The connection string is never read. | Tables and queries, ORMs, the database name. |
| .NET database clients | `AddDbContext`, `AddDbContextPool`, `AddDbContextFactory`, `AddPooledDbContextFactory` with the provider call inside them (`UseNpgsql`, `UseSqlServer`, `UseMySql`, `UseSqlite`, `UseOracle`, `UseCosmos`), the Aspire registrations (`AddNpgsqlDataSource`, `AddNpgsqlDbContext`, `AddSqlServerClient` and others), `NpgsqlDataSource.Create`, `ConnectionMultiplexer.Connect`, and `new` of `NpgsqlConnection`, `SqlConnection`, `MySqlConnection`, `SqliteConnection`, `MongoClient`. The store kind, and the connection name of `GetConnectionString("X")` or the configuration key it reads. | A connection string built in code. A registration in a helper method. |
| .NET HTTP clients | `AddHttpClient` and `ConfigureHttpClient` that set `BaseAddress` from a configuration key, such as `config["Payments:BaseUrl"]`. The key. | A literal address. An options class. |
| Relationships | Lines between elements, from the facts of the other rules. See [Relationships of a code scan](#relationships-of-a-code-scan). | HTTP calls from code paths other than URL variables and HTTP client base addresses. |
| Dockerfile | Base image, ports, the executable and flag names of the entrypoint. | `ARG`, `ENV`, substitution. |
| Kubernetes | Deployment, StatefulSet, DaemonSet, Service, Ingress: names, images, ports, replicas, the executable and flag names of a container command, Service selectors, Ingress rules. | Helm templates, Kustomize, other kinds. Environment variables, labels, annotations, volumes and init containers. |
| GitHub Actions | The workflow, its triggers and its jobs, with build and deploy jobs found by known actions and commands. The name of a job says nothing. | Reusable workflows, matrix expansion, composite actions. A command after `sudo` or `time`, in a script file, or not first on its line. `env`, `with`, `inputs` and `secrets` are never read. |

Code inside `#if false` produces nothing. Code inside `#if SYMBOL` produces facts with the condition recorded.

## Relationships of a code scan

A code scan proposes a relationship between two elements when the facts of the other rules show it. The key of a relationship names its rule and its two ends, so a later scan finds the same relationship.

A Compose service is an element when it is a data store, or when it builds one code service: the directory of its Dockerfile or of its build context holds the project file of that service. A Compose service with no build is the code service of the same name. A broker is no element.

| Relationship | Label and kind | Truth label | Evidence |
|---|---|---|---|
| A producer to each consumer of the same message. A consumer listens to the message, or to a queue that the channel of the message reaches through its bindings. A binding with a routing key passes only a message with a matching key. | `publishes <message>`, behavioural | Observed in source | The publish lines and the subscribe lines, and the binding lines of the route. |
| An app to the store it opens. The store is the one Compose store that the variable of the connection names as its host. Else it is the one store, of the same kind, that another variable names as its host or that `depends_on` names. Else it is the one store with the name of the connection. | `reads and writes`, behavioural | Observed in source | The lines that open the store, and the variable or `depends_on` line. |
| A Compose `depends_on` between two services that are elements. | `depends on`, structural | Observed in source | The `depends_on` line. |
| An app to another app whose Compose service name a variable or an HTTP client key names, followed by an address word: `PAYMENTS_URL`, `PAYMENTS__BASEURL`, `Services:Payments:Endpoint`. | `calls`, behavioural | Inferred, with confidence 0.6 for a variable and 0.8 for an HTTP client that reads it | The variable line and the HTTP client lines. |

Each relationship also cites the first line of each element at its ends, and of the message of a message flow. Those lines are never its source pointer.

A scan applies the first three kinds and follows the rules for [Not seen in latest scan](/how-to/import-a-repository/#not-seen-in-latest-scan). A scan never applies an inferred call. It waits in **Needs you** as a suggestion until a person accepts or rejects it.

A scan proposes no relationship that the model holds already, with the same two ends, kind and label. A relationship with another label is another relationship.

When an app opens a store and no rule names exactly one store, the scan proposes no relationship. A scan with a Compose file then adds the note `data-access-store-not-resolved`.

## Rule identifiers

Each rule has the version 2, but these: `go.sql-open` has the version 3; `go.struct-message`, `dotnet.db-client`, `dotnet.http-client-base-address` and the `relationship.*` rules have the version 1.

| Area | Rule identifiers |
|---|---|
| OpenAPI | `openapi.document`, `openapi.operation`, `openapi.schema` |
| AsyncAPI | `asyncapi.document`, `asyncapi.channel`, `asyncapi.message`, `asyncapi.operation`, `asyncapi.schema` |
| Docker Compose | `compose.service` |
| .NET | `dotnet.project`, `dotnet.package-reference`, `dotnet.minimal-api-route`, `dotnet.controller-route`, `dotnet.masstransit-consumer`, `dotnet.masstransit-receive-endpoint`, `dotnet.masstransit-publish`, `dotnet.masstransit-entity-name`, `dotnet.masstransit-outbox`, `dotnet.outbox-handwritten`, `dotnet.efcore-dbcontext`, `dotnet.message-contract`, `dotnet.db-client`, `dotnet.http-client-base-address` |
| Go | `go.module`, `go.require`, `go.net-http-route`, `go.chi-route`, `go.gin-route`, `go.echo-route`, `go.amqp-exchange-declare`, `go.amqp-queue-declare`, `go.amqp-queue-bind`, `go.amqp-publish`, `go.amqp-consume`, `go.kafka-topic`, `go.nats-subject`, `go.sql-open`, `go.struct-message` |
| Deployment | `docker.dockerfile`, `k8s.workload`, `k8s.service`, `k8s.ingress`, `github.workflow` |
| Relationships | `relationship.message-flow`, `relationship.data-access`, `relationship.compose-depends-on`, `relationship.http-call`, `relationship.basis` |

## Limits of one scan

| Limit | Default |
|---|---|
| Files | 2,000 |
| Size of one file | 256 KB |
| Total size | 20 MB |
| Facts | 20,000 |
| Facts from one file | 2,000 |
| Size of one schema body in a fact | 64 KB |
| Depth of a JSON or YAML document | 64 |
| Items in one list attribute, such as ports | 200 |
| Notes | 2,000 |

A workspace owner changes the first three limits in the settings: `import.maxFiles`, `import.maxFileBytes` and `import.maxTotalBytes`. See [Settings](/reference/settings/#import). The other limits are fixed.

A file over the size limit is not read, and the scan continues. A scan that reaches the limit of files or of total size reads no more files and extracts the files that it read. A scan that reaches the limit of facts stops and keeps the facts that it found.

A scan that reaches a limit is partial. It keeps what it found and says so.

## When the rules change

A new version of Vellum can change a rule. The next scan then matches the facts in the model to the facts it finds before it compares them. It proposes no removal because of a rule change. When it cannot match a fact exactly, the review asks you to choose.
