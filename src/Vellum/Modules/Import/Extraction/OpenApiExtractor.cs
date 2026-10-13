namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// OpenAPI 3.0 and 3.1, JSON or YAML. A file is an OpenAPI document when its root has an
/// <c>openapi</c> field. Facts:
/// <list type="bullet">
/// <item><c>contract:&lt;path&gt;</c> (Schema): title, versions, server URLs without user information or query.</item>
/// <item><c>operation:&lt;path&gt;:&lt;METHOD&gt; &lt;route&gt;</c> (Operation): the lines of the operation object.</item>
/// <item><c>schema:&lt;path&gt;#/components/schemas/&lt;Name&gt;</c> (Schema): the schema as sorted JSON.</item>
/// </list>
/// Not recognised: Swagger 2.0; a path item behind <c>$ref</c>; references to other files or
/// URLs (recorded as <c>external-ref</c>, never fetched); path-level parameters, callbacks,
/// webhooks and links; an inline schema body (recorded as <c>inline</c>).
/// </summary>
internal sealed class OpenApiExtractor : IExtractor
{
    private static readonly ExtractionRule Document = new("openapi.document", 1);
    private static readonly ExtractionRule Operation = new("openapi.operation", 1);
    private static readonly ExtractionRule Schema = new("openapi.schema", 1);

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    public string Id => "openapi";
    public IReadOnlyList<ExtractionRule> Rules => [Document, Operation, Schema];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (!file.IsJson && !file.IsYaml)
                continue;
            var root = context.Documents(file).FirstOrDefault();
            if (root is not { Kind: DocKind.Map })
                continue;
            if (root.Entry("swagger") is { } swagger)
            {
                context.Note(NoteKind.Undecided, "unsupported-openapi-version", file.Path, swagger.KeyLine);
                continue;
            }
            if (root.Entry("openapi") is not { } version)
                continue;
            if (version.Value.AsText() is not { } text || !text.StartsWith("3.", StringComparison.Ordinal))
            {
                context.Note(NoteKind.Undecided, "unsupported-openapi-version", file.Path, version.KeyLine);
                continue;
            }
            Extract(context, file, root, version, text);
        }
    }

    private static void Extract(ExtractionContext context, SourceText file, DocNode root, DocEntry version, string specVersion)
    {
        var contract = "contract:" + file.Path;
        var info = root.Map("info");
        var evidence = new List<SourceEvidence> { Document.At(file.Path, version.KeyLine, version.Value.EndLine) };
        var servers = new List<string>();
        if (root.Entry("servers") is { Value.Kind: DocKind.Seq } serverList)
        {
            evidence.Add(Document.At(file.Path, serverList.KeyLine, file.TrimEnd(serverList.KeyLine, serverList.Value.EndLine)));
            foreach (var server in serverList.Value.Items)
                if (server.Str("url") is { } url)
                    servers.Add(CredentialGuard.ScrubUrl(url) ?? url);
        }
        context.Add(FactKind.Schema, contract, info?.Str("title") ?? file.Path, evidence,
        [
            ("format", "openapi"), ("specVersion", specVersion),
            ("title", info?.Str("title")), ("apiVersion", info?.Str("version")),
            ("servers", TextUtil.Join(servers)),
        ]);

        var globalSecurity = HasSecurity(root.Get("security"));
        foreach (var path in root.Map("paths")?.Entries ?? [])
        {
            if (path.Value.Kind != DocKind.Map || !path.Key.StartsWith('/'))
                continue;
            if (path.Value.Entry("$ref") is { } reference)
            {
                context.Note(NoteKind.Undecided, "external-ref", file.Path, reference.KeyLine);
                continue;
            }
            foreach (var method in Methods)
            {
                if (path.Value.Entry(method) is not { Value.Kind: DocKind.Map } operation)
                    continue;
                AddOperation(context, file, contract, path.Key, method.ToUpperInvariant(), operation, globalSecurity);
            }
        }

        ContractSchemas.Add(context, file, root, contract, Schema);
    }

    private static void AddOperation(
        ExtractionContext context, SourceText file, string contract, string path, string method, DocEntry entry, bool globalSecurity)
    {
        var operation = entry.Value;
        var attributes = new List<(string, string?)>
        {
            ("contract", contract), ("transport", "http"), ("method", method), ("path", path),
            ("operationId", operation.Str("operationId")),
            ("summary", operation.Str("summary") is { } summary ? TextUtil.Bounded(summary, 200) : null),
            ("security", (operation.Get("security") is { } security ? HasSecurity(security) : globalSecurity) ? "true" : "false"),
        };
        if (operation.Str("deprecated") == "true")
            attributes.Add(("deprecated", "true"));
        if (operation.Seq("tags") is { } tags)
            attributes.Add(("tags", TextUtil.Join(tags.Items.Select(t => t.AsText()).OfType<string>())));

        if (Media(operation.Map("requestBody")) is ({ } contentType, var schema))
        {
            attributes.Add(("requestContentType", contentType));
            attributes.Add(("requestSchema", ContractSchemas.Reference(file, schema)));
        }

        var codes = new List<string>();
        foreach (var response in operation.Map("responses")?.Entries ?? [])
        {
            codes.Add(response.Key);
            if (response.Value.Kind == DocKind.Map && Media(response.Value) is (_, var responseSchema))
                attributes.Add(($"response.{response.Key}.schema", ContractSchemas.Reference(file, responseSchema)));
        }
        attributes.Add(("responses", TextUtil.JoinSorted(codes)));

        context.Add(FactKind.Operation, $"operation:{file.Path}:{method} {path}", $"{method} {path}",
            [Operation.At(file.Path, entry.KeyLine, file.TrimEnd(entry.KeyLine, operation.EndLine))], attributes);
    }

    /// <summary>The JSON media type of a request body or response, or the first media type in name order.</summary>
    private static (string ContentType, DocNode? Schema)? Media(DocNode? body)
    {
        if (body?.Map("content") is not { Entries.Count: > 0 } content)
            return null;
        var chosen = content.Entry("application/json") ?? content.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).First();
        return (chosen.Key, chosen.Value.Kind == DocKind.Map ? chosen.Value.Get("schema") : null);
    }

    private static bool HasSecurity(DocNode? security) =>
        security is { Kind: DocKind.Seq } && security.Items.Any(item => item is { Kind: DocKind.Map, Entries.Count: > 0 });
}

/// <summary>Component schemas, shared by the OpenAPI and AsyncAPI extractors.</summary>
internal static class ContractSchemas
{
    public static void Add(ExtractionContext context, SourceText file, DocNode root, string contract, ExtractionRule rule)
    {
        foreach (var schema in root.Map("components")?.Map("schemas")?.Entries ?? [])
        {
            var pointer = "#/components/schemas/" + schema.Key;
            var body = StructuredDocument.ToCanonicalJson(schema.Value, context.Limits.MaxSchemaBytes);
            if (body is null)
                context.LimitReached("schema-bytes", file.Path, stop: false, schema.KeyLine);
            context.Add(FactKind.Schema, $"schema:{file.Path}{pointer}", schema.Key,
                rule.At(file.Path, schema.KeyLine, file.TrimEnd(schema.KeyLine, schema.Value.EndLine)),
                ("contract", contract), ("format", "json-schema"), ("pointer", pointer), ("schema", body));
        }
    }

    /// <summary>
    /// The key of the schema a node refers to, <c>inline</c> for a schema written in place,
    /// <c>external-ref</c> for a reference that leaves the file, or null when there is no schema.
    /// </summary>
    public static string? Reference(SourceText file, DocNode? schema)
    {
        if (schema is not { Kind: DocKind.Map })
            return null;
        if (schema.Str("$ref") is not { } reference)
            return "inline";
        return reference.StartsWith("#/", StringComparison.Ordinal) ? $"schema:{file.Path}{reference}" : "external-ref";
    }
}
