namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// OpenAPI 3.0 and 3.1, JSON or YAML. A file is an OpenAPI document when exactly one of its
/// documents is a mapping with an <c>openapi</c> field (see <see cref="DocumentSet.Single"/>).
/// All rules have version 2.
/// <list type="bullet">
/// <item><c>openapi.document</c>: <c>contract:&lt;path&gt;</c> (Schema) with <c>format=openapi</c>,
/// <c>specVersion</c>, <c>title</c>, <c>apiVersion</c> and <c>servers</c>. A server URL is
/// reduced with <see cref="CredentialGuard.SafeUrl"/> to scheme, host, port and path. A URL
/// that cannot be parsed safely gives nothing and a <c>server-url-not-safe</c> note.</item>
/// <item><c>openapi.operation</c>: <c>operation:&lt;path&gt;:&lt;METHOD&gt; &lt;normalised route&gt;</c>
/// (Operation) for each of get, put, post, delete, options, head, patch and trace of each key
/// of <c>paths</c> that starts with '/'. The route is normalised with
/// <see cref="RoutePath.Normalize"/>; the attributes <c>path</c>, <c>routeTemplate</c> and
/// <c>declaredRoute</c> come from <see cref="RoutePath.Attributes"/>. Two keys of one document
/// that normalise to one route give one fact with a conflict on <c>declaredRoute</c>.
/// <list type="bullet">
/// <item><c>security</c>: the <c>security</c> field of the operation, or the one of the document
/// when the operation has none. <c>none</c> when it is missing or an empty list,
/// <c>optional</c> when one of its items is an empty requirement (<c>{}</c>, anonymous access
/// is allowed), <c>required</c> otherwise.</item>
/// <item><c>deprecated=true</c> when the field is a boolean that is true, in any YAML spelling.</item>
/// <item><c>requestSchema</c> and <c>response.&lt;code&gt;.schema</c>: the key of the schema fact
/// for a <c>$ref</c> to <c>#/components/schemas/&lt;Name&gt;</c> that exists in the document;
/// <c>inline</c> for a schema written in place; <c>external-ref</c> for a reference that leaves
/// the file (it is never fetched). A reference to a schema that is not in the document gives no
/// attribute and a <c>dangling-ref</c> note; another local pointer gives an
/// <c>unsupported-ref</c> note. The media type is <c>application/json</c> when the content has
/// it, else the first one in name order.</item>
/// <item>A path item that is a <c>$ref</c> inside the document is read where it is defined,
/// through at most <see cref="MaxReferenceHops"/> references. The evidence is the lines of the
/// path key and the lines of the operation. Notes, and no fact: <c>external-ref</c> (leaves the
/// file), <c>dangling-ref</c>, <c>ref-cycle</c>, <c>ref-too-deep</c>, and
/// <c>ref-with-siblings</c> (operations beside the <c>$ref</c>).</item>
/// <item>An <c>operationId</c> that a second operation of the document repeats gives a
/// <c>duplicate-operation-id</c> note. Keys hold the method and the route, so the facts do not collide.</item>
/// </list></item>
/// <item><c>openapi.schema</c>: <c>schema:&lt;path&gt;#/components/schemas/&lt;Name&gt;</c> (Schema)
/// with the body that <see cref="SchemaBody"/> writes: structure, and no value.</item>
/// </list>
/// Not recognised: Swagger 2.0 (<c>unsupported-openapi-version</c>); a file with two OpenAPI
/// documents (<c>multiple-documents</c>); references to other files or URLs; a request body or
/// a response behind <c>$ref</c>; path-level parameters, callbacks, webhooks and links; the
/// body of an inline schema; response codes other than <c>default</c> and three characters of
/// digits and 'X'; the security schemes themselves.
/// </summary>
internal sealed class OpenApiExtractor : IExtractor
{
    private static readonly ExtractionRule Document = new("openapi.document", 2);
    private static readonly ExtractionRule Operation = new("openapi.operation", 2);
    private static readonly ExtractionRule Schema = new("openapi.schema", 2);

    /// <summary>References that are followed from a path item before the path is given up.</summary>
    internal const int MaxReferenceHops = 8;

    private static readonly Dictionary<string, string> Methods = new(StringComparer.Ordinal)
    {
        ["get"] = "GET", ["put"] = "PUT", ["post"] = "POST", ["delete"] = "DELETE",
        ["options"] = "OPTIONS", ["head"] = "HEAD", ["patch"] = "PATCH", ["trace"] = "TRACE",
    };

    public string Id => "openapi";
    public IReadOnlyList<ExtractionRule> Rules => [Document, Operation, Schema];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (file.IsJson || file.IsYaml)
                context.Guarded(Id, file.Path, () => Read(context, file));
        }
    }

    private static void Read(ExtractionContext context, SourceText file)
    {
        if (DocumentSet.Single(context, file, document => document.Get("openapi") is not null || document.Get("swagger") is not null) is not { } root)
            return;
        if (root.Entry("swagger") is { } swagger)
        {
            context.Note(NoteKind.Undecided, "unsupported-openapi-version", file.Path, swagger.KeyLine);
            return;
        }
        var version = root.Entry("openapi")!;
        if (version.Value.AsText() is not { } text || !text.StartsWith("3.", StringComparison.Ordinal))
        {
            context.Note(NoteKind.Undecided, "unsupported-openapi-version", file.Path, version.KeyLine);
            return;
        }
        new Reader(context, file, root).Extract(version, text);
    }

    private sealed class Reader(ExtractionContext context, SourceText file, DocNode root)
    {
        private readonly string _contract = "contract:" + file.Path;
        private readonly WorkMeter _meter = context.Meter(file);
        private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);

        public void Extract(DocEntry version, string specVersion)
        {
            var info = root.Map("info");
            var title = ContractSchemas.Title(context, file, info);
            var evidence = new List<SourceEvidence> { Document.At(file.Path, version.KeyLine, version.Value.EndLine) };
            var servers = new List<string>();
            if (root.Entry("servers") is { Value.Kind: DocKind.Seq } serverList)
            {
                if (!_meter.Charge(serverList.Value.Items.Count))
                    return;
                evidence.Add(Document.At(file.Path, serverList.KeyLine, serverList.Value.EndLine));
                foreach (var server in serverList.Value.Items)
                {
                    if (server.Str("url") is not { } url)
                        continue;
                    if (CredentialGuard.SafeUrl(url) is { } safe)
                        servers.Add(safe);
                    else
                        context.Note(NoteKind.Undecided, "server-url-not-safe", file.Path, server.StartLine);
                }
            }
            context.Add(FactKind.Schema, _contract, title ?? file.Path, evidence,
            [
                ("format", "openapi"), ("specVersion", specVersion),
                ("title", title), ("apiVersion", info?.Str("version")),
                ("servers", context.List(servers, file.Path, version.KeyLine)),
            ]);

            var globalSecurity = Security(root.Get("security")) ?? "none";
            foreach (var path in root.Map("paths")?.Entries ?? [])
            {
                if (context.Stopped || !_meter.Charge())
                    return;
                if (path.Value.Kind != DocKind.Map || !path.Key.StartsWith('/'))
                    continue;
                if (PathItem(path) is not { } item)
                    continue;
                if (!_meter.Charge(item.Entries.Count))
                    return;
                var site = ReferenceEquals(item, path.Value) ? null : Operation.At(file.Path, path.KeyLine, path.Value.EndLine);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in item.Entries)
                    if (entry.Value.Kind == DocKind.Map && Methods.TryGetValue(entry.Key, out var method) && seen.Add(method))
                        AddOperation(path.Key, method, entry, site, globalSecurity);
            }

            ContractSchemas.Add(context, file, root, _contract, Schema);
        }

        /// <summary>The path item of a key of <c>paths</c>: the mapping itself, or the one its <c>$ref</c> leads to inside the document.</summary>
        private DocNode? PathItem(DocEntry path)
        {
            var node = path.Value;
            if (node.Entry("$ref") is not { } site)
                return node;
            var visited = new HashSet<DocNode>(ReferenceEqualityComparer.Instance) { node };
            for (var hop = 0; hop < MaxReferenceHops; hop++)
            {
                if (node.Kind != DocKind.Map)
                    return Undecided("dangling-ref");
                if (node.Entry("$ref") is not { } reference)
                    return node;
                if (!_meter.Charge(node.Entries.Count + 1))
                    return null;
                foreach (var entry in node.Entries)
                    if (Methods.ContainsKey(entry.Key))
                        return Undecided("ref-with-siblings");
                if (reference.Value.AsText() is not { } text || !text.StartsWith('#'))
                    return Undecided("external-ref");
                if (JsonPointer.Segments(text) is not { } segments || JsonPointer.Resolve(root, segments) is not { } target)
                    return Undecided("dangling-ref");
                if (!visited.Add(target))
                    return Undecided("ref-cycle");
                node = target;
            }
            return node.Kind == DocKind.Map && node.Entry("$ref") is null ? node : Undecided("ref-too-deep");

            DocNode? Undecided(string reason)
            {
                context.Note(NoteKind.Undecided, reason, file.Path, site.KeyLine);
                return null;
            }
        }

        private void AddOperation(string declared, string method, DocEntry entry, SourceEvidence? site, string globalSecurity)
        {
            var operation = entry.Value;
            if (!_meter.Charge(operation.Entries.Count + 1))
                return;
            var path = RoutePath.Normalize(declared);
            var attributes = new List<(string, string?)>
            {
                ("contract", _contract), ("transport", "http"), ("method", method),
                ("summary", operation.Str("summary") is { } summary ? TextUtil.Bounded(summary, 200) : null),
                ("security", Security(operation.Get("security")) ?? globalSecurity),
            };
            attributes.AddRange(RoutePath.Attributes(path, declared));
            if (operation.Entry("operationId") is { } operationId && operationId.Value.AsText() is { } id)
            {
                attributes.Add(("operationId", id));
                if (!_operationIds.Add(id))
                    context.Note(NoteKind.Undecided, "duplicate-operation-id", file.Path, operationId.KeyLine);
            }
            if (operation.Get("deprecated") is { Kind: DocKind.Scalar, Type: ScalarType.Bool } deprecated
                && deprecated.Text.Equals("true", StringComparison.OrdinalIgnoreCase))
                attributes.Add(("deprecated", "true"));
            if (operation.Seq("tags") is { } tags)
            {
                if (!_meter.Charge(tags.Items.Count))
                    return;
                attributes.Add(("tags", context.List(tags.Items.Select(t => t.AsText()).OfType<string>(), file.Path, entry.KeyLine)));
            }

            if (Media(operation.Map("requestBody")) is ({ } contentType, var schema))
            {
                attributes.Add(("requestContentType", contentType));
                attributes.Add(("requestSchema", ContractSchemas.Reference(context, file, root, schema)));
            }

            var codes = new List<string>();
            var responses = operation.Map("responses")?.Entries ?? [];
            if (!_meter.Charge(responses.Count))
                return;
            foreach (var response in responses)
            {
                if (!IsResponseCode(response.Key) || codes.Contains(response.Key))
                    continue;
                if (codes.Count >= context.Limits.MaxListItems)
                {
                    context.LimitReached("list-items", file.Path, stop: false, entry.KeyLine);
                    break;
                }
                codes.Add(response.Key);
                if (response.Value.Kind == DocKind.Map && Media(response.Value) is (_, var responseSchema))
                    attributes.Add(($"response.{response.Key}.schema", ContractSchemas.Reference(context, file, root, responseSchema)));
            }
            attributes.Add(("responses", context.List(codes, file.Path, entry.KeyLine, sorted: true)));

            var at = Operation.At(file.Path, entry.KeyLine, operation.EndLine);
            context.Add(FactKind.Operation, $"operation:{file.Path}:{method} {path}", $"{method} {path}", site is null ? [at] : [site, at], attributes);
        }

        /// <summary>The JSON media type of a request body or response, or the first media type in name order.</summary>
        private (string? ContentType, DocNode? Schema)? Media(DocNode? body)
        {
            if (body?.Map("content") is not { Entries.Count: > 0 } content || !_meter.Charge(content.Entries.Count))
                return null;
            var chosen = content.Entry("application/json");
            if (chosen is null)
                foreach (var entry in content.Entries)
                    if (chosen is null || string.CompareOrdinal(entry.Key, chosen.Key) < 0)
                        chosen = entry;
            return (IsMediaType(chosen!.Key) ? chosen.Key : null, chosen.Value.Kind == DocKind.Map ? chosen.Value.Get("schema") : null);
        }

        /// <summary><c>none</c>, <c>optional</c> or <c>required</c>; null when there is no <c>security</c> field.</summary>
        private string? Security(DocNode? security)
        {
            if (security is null)
                return null;
            if (security.Kind != DocKind.Seq || security.Items.Count == 0 || !_meter.Charge(security.Items.Count))
                return "none";
            foreach (var item in security.Items)
                if (item is { Kind: DocKind.Map, Entries.Count: 0 })
                    return "optional";
            return "required";
        }

        private static bool IsResponseCode(string code)
        {
            if (code == "default")
                return true;
            if (code.Length != 3 || code[0] is < '1' or > '5')
                return false;
            return code[1] is >= '0' and <= '9' or 'X' && code[2] is >= '0' and <= '9' or 'X';
        }
    }

    /// <summary>A media type name: letters, digits and <c>. + * / _ - ; = space</c>, at most 100 characters.</summary>
    internal static bool IsMediaType(string name)
    {
        if (name.Length is 0 or > 100)
            return false;
        foreach (var c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '*' or '/' or '_' or '-' or ';' or '=' or ' '))
                return false;
        return true;
    }
}

/// <summary>Component schemas, shared by the OpenAPI and AsyncAPI extractors.</summary>
internal static class ContractSchemas
{
    private const string Prefix = "#/components/schemas/";

    /// <summary>
    /// <c>info.title</c>, which is also the name of the contract fact. A title that looks like a
    /// credential is left out with a <c>title-not-emitted</c> note, and the fact is named by its file.
    /// </summary>
    public static string? Title(ExtractionContext context, SourceText file, DocNode? info)
    {
        if (info?.Entry("title") is not { } entry || entry.Value.AsText() is not { Length: > 0 and <= 256 } title)
            return null;
        if (!CredentialGuard.LooksSecret(title))
            return title;
        context.Note(NoteKind.Redacted, "title-not-emitted", file.Path, entry.KeyLine);
        return null;
    }

    /// <summary>One Schema fact for each entry of <c>components.schemas</c>, with its body.</summary>
    public static void Add(ExtractionContext context, SourceText file, DocNode root, string contract, ExtractionRule rule, SchemaBody? bodies = null)
    {
        bodies ??= new SchemaBody(context, file);
        var meter = context.Meter(file);
        foreach (var schema in root.Map("components")?.Map("schemas")?.Entries ?? [])
        {
            if (context.Stopped || !meter.Charge())
                return;
            var pointer = Prefix + schema.Key;
            context.Add(FactKind.Schema, $"schema:{file.Path}{pointer}", schema.Key,
                rule.At(file.Path, schema.KeyLine, schema.Value.EndLine),
                ("contract", contract), ("format", "json-schema"), ("pointer", pointer), ("schema", bodies.Write(schema.Value, schema.KeyLine)));
        }
    }

    /// <summary>
    /// What a schema site refers to: the key of the schema fact for a <c>$ref</c> to a component
    /// schema that the document has, <c>inline</c> for a schema written in place,
    /// <c>external-ref</c> for a reference that leaves the file, or null when there is no schema.
    /// A reference to a component schema that the document does not have gives null and a
    /// <c>dangling-ref</c> note; another pointer into the document gives null and an
    /// <c>unsupported-ref</c> note.
    /// </summary>
    public static string? Reference(ExtractionContext context, SourceText file, DocNode root, DocNode? schema)
    {
        if (schema is not { Kind: DocKind.Map })
            return null;
        if (schema.Entry("$ref") is not { } entry)
            return "inline";
        if (entry.Value.AsText() is not { } reference)
            return null;
        if (!reference.StartsWith('#'))
            return "external-ref";
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal) || JsonPointer.Segments(reference) is not { Count: 3 } segments)
        {
            context.Note(NoteKind.Undecided, "unsupported-ref", file.Path, entry.KeyLine);
            return null;
        }
        if (root.Map("components")?.Map("schemas")?.Entry(segments[2]) is not { } target)
        {
            context.Note(NoteKind.Undecided, "dangling-ref", file.Path, entry.KeyLine);
            return null;
        }
        return $"schema:{file.Path}{Prefix}{target.Key}";
    }
}
