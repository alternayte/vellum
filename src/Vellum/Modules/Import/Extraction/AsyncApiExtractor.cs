namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// AsyncAPI 2.x and 3.x, JSON or YAML. A file is an AsyncAPI document when its root has an
/// <c>asyncapi</c> field. Facts:
/// <list type="bullet">
/// <item><c>contract:&lt;path&gt;</c> (Schema): title, versions, server names and protocols.</item>
/// <item><c>channel:&lt;address&gt;</c> (Channel): messages, and the AMQP or Kafka binding when there is one.</item>
/// <item><c>message:&lt;name&gt;</c> (Message): content type and payload schema.</item>
/// <item><c>publication:&lt;path&gt;:&lt;operation&gt;</c> and <c>subscription:&lt;path&gt;:&lt;operation&gt;</c>.</item>
/// <item><c>schema:&lt;path&gt;#/components/schemas/&lt;Name&gt;</c> (Schema).</item>
/// </list>
/// In 3.x, <c>send</c> is a publication and <c>receive</c> is a subscription. In 2.x the
/// words describe the other party: a <c>subscribe</c> operation means the application
/// publishes, and <c>publish</c> means it subscribes. The <c>action</c> attribute holds the
/// word the document uses.
/// Not recognised: references to other files or URLs; traits; server and message bindings;
/// bindings other than AMQP and Kafka; correlation IDs, headers and reply operations.
/// </summary>
internal sealed class AsyncApiExtractor : IExtractor
{
    private static readonly ExtractionRule Document = new("asyncapi.document", 1);
    private static readonly ExtractionRule Channel = new("asyncapi.channel", 1);
    private static readonly ExtractionRule Message = new("asyncapi.message", 1);
    private static readonly ExtractionRule Operation = new("asyncapi.operation", 1);
    private static readonly ExtractionRule Schema = new("asyncapi.schema", 1);

    private const int MaxReferenceHops = 8;

    public string Id => "asyncapi";
    public IReadOnlyList<ExtractionRule> Rules => [Document, Channel, Message, Operation, Schema];

    public void Run(ExtractionContext context)
    {
        foreach (var file in context.Files)
        {
            if (context.Stopped)
                return;
            if (!file.IsJson && !file.IsYaml)
                continue;
            var root = context.Documents(file).FirstOrDefault();
            if (root is not { Kind: DocKind.Map } || root.Entry("asyncapi") is not { } version)
                continue;
            var text = version.Value.AsText() ?? "";
            var major = text.StartsWith("2.", StringComparison.Ordinal) ? 2 : text.StartsWith("3.", StringComparison.Ordinal) ? 3 : 0;
            if (major == 0)
            {
                context.Note(NoteKind.Undecided, "unsupported-asyncapi-version", file.Path, version.KeyLine);
                continue;
            }
            new Reader(context, file, root).Extract(version, text, major);
        }
    }

    private sealed class Reader(ExtractionContext context, SourceText file, DocNode root)
    {
        private readonly string _contract = "contract:" + file.Path;

        public void Extract(DocEntry version, string specVersion, int major)
        {
            var info = root.Map("info");
            var servers = root.Map("servers")?.Entries ?? [];
            context.Add(FactKind.Schema, _contract, info?.Str("title") ?? file.Path,
                [Document.At(file.Path, version.KeyLine, version.Value.EndLine)],
                [
                    ("format", "asyncapi"), ("specVersion", specVersion),
                    ("title", info?.Str("title")), ("apiVersion", info?.Str("version")),
                    ("servers", TextUtil.Join(servers.Select(s => s.Key))),
                    ("protocols", TextUtil.JoinSorted(servers.Select(s => s.Value.Kind == DocKind.Map ? s.Value.Str("protocol") : null).OfType<string>())),
                ]);

            foreach (var message in root.Map("components")?.Map("messages")?.Entries ?? [])
                AddMessage(message.Value, message.Key, message.KeyLine);

            if (major == 3)
                ExtractV3();
            else
                ExtractV2();

            ContractSchemas.Add(context, file, root, _contract, Schema);
        }

        private void ExtractV3()
        {
            var channelKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            var channelMessages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var channel in root.Map("channels")?.Entries ?? [])
            {
                if (channel.Value.Kind != DocKind.Map)
                    continue;
                var address = channel.Value.Str("address") ?? channel.Key;
                var key = "channel:" + address;
                var messages = new List<string>();
                foreach (var message in channel.Value.Map("messages")?.Entries ?? [])
                    if (AddMessage(message.Value, message.Key, message.KeyLine) is { } messageKey)
                        messages.Add(messageKey);
                channelKeys[channel.Key] = key;
                channelMessages[channel.Key] = messages;

                var attributes = new List<(string, string?)>
                {
                    ("contract", _contract), ("channelId", channel.Key), ("address", address),
                    ("messages", TextUtil.JoinSorted(messages)),
                };
                AddBindings(attributes, channel.Value.Map("bindings"));
                context.Add(FactKind.Channel, key, channel.Key,
                    [Channel.At(file.Path, channel.KeyLine, file.TrimEnd(channel.KeyLine, channel.Value.EndLine))], attributes);
            }

            foreach (var operation in root.Map("operations")?.Entries ?? [])
            {
                if (operation.Value.Kind != DocKind.Map)
                    continue;
                var action = operation.Value.Str("action");
                if (action is not ("send" or "receive"))
                {
                    context.Note(NoteKind.Undecided, "unknown-asyncapi-action", file.Path, operation.KeyLine);
                    continue;
                }
                var channelId = LastSegment(operation.Value.Map("channel")?.Str("$ref"), "#/channels/");
                if (channelId is null || !channelKeys.TryGetValue(channelId, out var channelKey))
                {
                    context.Note(NoteKind.Undecided, "unresolved-channel", file.Path, operation.KeyLine);
                    continue;
                }

                List<string> messages;
                if (operation.Value.Seq("messages") is { } listed)
                {
                    messages = [];
                    foreach (var item in listed.Items)
                        if (Resolve(item) is ({ } node, var id) && MessageName(node, id) is { } name)
                            messages.Add("message:" + name);
                }
                else
                {
                    messages = channelMessages[channelId];
                }
                AddOperation(action == "send" ? FactKind.Publication : FactKind.Subscription,
                    operation.Key, operation, action, channelKey, messages);
            }
        }

        private void ExtractV2()
        {
            foreach (var channel in root.Map("channels")?.Entries ?? [])
            {
                if (channel.Value.Kind != DocKind.Map)
                    continue;
                var key = "channel:" + channel.Key;
                var all = new List<string>();
                foreach (var action in (string[])["publish", "subscribe"])
                {
                    if (channel.Value.Entry(action) is not { Value.Kind: DocKind.Map } operation)
                        continue;
                    var messages = new List<string>();
                    if (operation.Value.Entry("message") is { } message)
                    {
                        var alternatives = message.Value.Kind == DocKind.Map && message.Value.Seq("oneOf") is { } oneOf
                            ? oneOf.Items : [message.Value];
                        foreach (var alternative in alternatives)
                            if (AddMessage(alternative, null, alternative.StartLine) is { } messageKey)
                                messages.Add(messageKey);
                    }
                    all.AddRange(messages);
                    // 2.x names the action of the other party.
                    AddOperation(action == "subscribe" ? FactKind.Publication : FactKind.Subscription,
                        operation.Value.Str("operationId") ?? $"{channel.Key}:{action}", operation, action, key, messages);
                }

                var attributes = new List<(string, string?)>
                {
                    ("contract", _contract), ("address", channel.Key), ("messages", TextUtil.JoinSorted(all)),
                };
                AddBindings(attributes, channel.Value.Map("bindings"));
                context.Add(FactKind.Channel, key, channel.Key,
                    [Channel.At(file.Path, channel.KeyLine, file.TrimEnd(channel.KeyLine, channel.Value.EndLine))], attributes);
            }
        }

        private void AddOperation(FactKind kind, string id, DocEntry operation, string action, string channelKey, List<string> messages)
        {
            var attributes = new List<(string, string?)>
            {
                ("contract", _contract), ("action", action), ("channel", channelKey),
                ("messages", TextUtil.JoinSorted(messages)),
                ("summary", operation.Value.Str("summary") is { } summary ? TextUtil.Bounded(summary, 200) : null),
            };
            var bindings = operation.Value.Map("bindings");
            if (bindings?.Map("amqp")?.Seq("cc") is { } routingKeys)
                attributes.Add(("amqp.routingKeys", TextUtil.Join(routingKeys.Items.Select(i => i.AsText()).OfType<string>())));
            if (bindings?.Map("kafka")?.Get("groupId") is { } group)
                attributes.Add(("kafka.groupId", group.AsText() ?? (group.Kind == DocKind.Map ? group.Seq("enum")?.Items.FirstOrDefault()?.AsText() : null)));

            var prefix = kind == FactKind.Publication ? "publication" : "subscription";
            context.Add(kind, $"{prefix}:{file.Path}:{id}", id,
                [Operation.At(file.Path, operation.KeyLine, file.TrimEnd(operation.KeyLine, operation.Value.EndLine))], attributes);
        }

        /// <summary>Adds the message a node holds or refers to, and returns its key.</summary>
        private string? AddMessage(DocNode site, string? id, int line)
        {
            if (Resolve(site) is not ({ Kind: DocKind.Map } node, var referenceId))
            {
                context.Note(NoteKind.Undecided, "unresolved-message", file.Path, line);
                return null;
            }
            if (MessageName(node, referenceId ?? id) is not { } name)
            {
                context.Note(NoteKind.Undecided, "unnamed-message", file.Path, line);
                return null;
            }
            // A reference names a message that is added where it is defined.
            if (!ReferenceEquals(node, site))
                return "message:" + name;

            var attributes = new List<(string, string?)>
            {
                ("contract", _contract), ("contentType", node.Str("contentType")), ("title", node.Str("title")),
            };
            // AsyncAPI 3 with a multi-format schema keeps the schema one level down.
            var payload = node.Get("payload");
            if (payload is { Kind: DocKind.Map } && payload.Get("schemaFormat") is not null && payload.Get("schema") is { } inner)
                payload = inner;
            if (ContractSchemas.Reference(file, payload) is { } reference)
            {
                attributes.Add(("payloadSchema", reference));
                if (reference == "inline")
                {
                    var body = StructuredDocument.ToCanonicalJson(payload!, context.Limits.MaxSchemaBytes);
                    if (body is null)
                        context.LimitReached("schema-bytes", file.Path, stop: false, payload!.StartLine);
                    attributes.Add(("payload", body));
                }
            }
            context.Add(FactKind.Message, "message:" + name, name,
                [Message.At(file.Path, line, file.TrimEnd(line, node.EndLine))], attributes);
            return "message:" + name;
        }

        private static string? MessageName(DocNode message, string? id) =>
            message.Str("name") ?? message.Str("messageId") ?? id;

        /// <summary>
        /// Follows <c>$ref</c> inside the document. Gives the node and the last segment of the
        /// last pointer, or null when a reference leaves the file, does not resolve or loops.
        /// </summary>
        private (DocNode? Node, string? Id)? Resolve(DocNode node)
        {
            string? id = null;
            for (var hop = 0; hop <= MaxReferenceHops; hop++)
            {
                if (node.Kind != DocKind.Map || node.Str("$ref") is not { } reference)
                    return (node, id);
                if (!reference.StartsWith("#/", StringComparison.Ordinal))
                    return null;
                var target = root;
                foreach (var raw in reference[2..].Split('/'))
                {
                    id = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    if (target.Get(id) is not { } next)
                        return null;
                    target = next;
                }
                node = target;
            }
            return null;
        }

        private static string? LastSegment(string? reference, string prefix) =>
            reference is not null && reference.StartsWith(prefix, StringComparison.Ordinal) && reference.IndexOf('/', prefix.Length) < 0
                ? reference[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : null;

        private static void AddBindings(List<(string, string?)> attributes, DocNode? bindings)
        {
            var amqp = bindings?.Map("amqp");
            var kafka = bindings?.Map("kafka");
            if (amqp is not null)
            {
                var role = amqp.Str("is");
                attributes.Add(("transport", "amqp"));
                attributes.Add(("amqp.is", role));
                attributes.Add(("kind", role == "queue" ? "queue" : role == "routingKey" ? "exchange" : null));
                attributes.Add(("amqp.exchange", amqp.Map("exchange")?.Str("name")));
                attributes.Add(("amqp.exchange.type", amqp.Map("exchange")?.Str("type")));
                attributes.Add(("amqp.queue", amqp.Map("queue")?.Str("name")));
            }
            else if (kafka is not null)
            {
                attributes.Add(("transport", "kafka"));
                attributes.Add(("kind", "topic"));
                attributes.Add(("kafka.topic", kafka.Str("topic")));
            }
        }
    }
}
