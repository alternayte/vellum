namespace Vellum.Modules.Import.Extraction;

/// <summary>
/// AsyncAPI 2.x and 3.x, JSON or YAML. A file is an AsyncAPI document when exactly one of its
/// documents is a mapping with an <c>asyncapi</c> field (see <see cref="DocumentSet.Single"/>).
/// All rules have version 2. Every key holds the path of the file: an entry of a document and
/// a type in code are different claims and never share a key.
/// <list type="bullet">
/// <item><c>asyncapi.document</c>: <c>contract:&lt;path&gt;</c> (Schema) with <c>format=asyncapi</c>,
/// <c>specVersion</c>, <c>title</c>, <c>apiVersion</c>, and for the servers in name order:
/// <c>servers</c> (names), <c>protocols</c> and <c>serverUrls</c>. A server address is
/// <c>url</c> in 2.x and <c>host</c> with <c>pathname</c> in 3.x; it is reduced with
/// <see cref="CredentialGuard.SafeUrl"/> to scheme, host, port and path. An address that cannot
/// be parsed safely gives nothing and a <c>server-url-not-safe</c> note.</item>
/// <item><c>asyncapi.channel</c>: <c>channel:&lt;path&gt;#&lt;channel id&gt;</c> (Channel). The
/// channel id is the key in <c>channels</c>. The broker address is the attribute
/// <c>address</c>: the <c>address</c> field in 3.x (no attribute when the field is missing),
/// the channel name in 2.x. <c>messages</c> lists the keys of the message facts.</item>
/// <item><c>asyncapi.message</c>: <c>message:&lt;path&gt;#&lt;message id&gt;</c> (Message) with
/// <c>name</c>, <c>title</c>, <c>contentType</c>, <c>payloadSchema</c> and, for a schema written
/// in place, <c>payload</c> (the body that <see cref="SchemaBody"/> writes: structure, no value).
/// The message id is:
/// <list type="bullet">
/// <item>the key in <c>components.messages</c> for a component message;</item>
/// <item><c>&lt;channel id&gt;/&lt;key&gt;</c> for a message written in the <c>messages</c> of a 3.x channel;</item>
/// <item><c>&lt;channel id&gt;/&lt;messageId or name&gt;</c> for a message written in a 2.x
/// operation (a message with neither gives an <c>unnamed-message</c> note).</item>
/// </list>
/// A <c>$ref</c> is followed inside the document through at most <see cref="MaxReferenceHops"/>
/// references and names the message where it is defined. A reference that leaves the file,
/// does not resolve, loops, or does not end at a component message or a channel message gives
/// an <c>unresolved-message</c> note and names nothing.</item>
/// <item><c>asyncapi.operation</c>: <c>publication:&lt;path&gt;:&lt;operation id&gt;</c> and
/// <c>subscription:&lt;path&gt;:&lt;operation id&gt;</c>. In 3.x, <c>send</c> is a publication and
/// <c>receive</c> is a subscription; the operation id is the key in <c>operations</c>. In 2.x
/// the words describe the other party: a <c>subscribe</c> operation means the application
/// publishes, and <c>publish</c> means it subscribes; the operation id is <c>operationId</c>, or
/// <c>&lt;channel&gt;:&lt;word&gt;</c> without one. <c>action</c> holds the word the document uses.</item>
/// <item><c>asyncapi.schema</c>: <c>schema:&lt;path&gt;#/components/schemas/&lt;Name&gt;</c> (Schema).</item>
/// </list>
/// <para>
/// Bindings give the values of a fixed list of fields and only when the value is an identifier
/// token (<see cref="CredentialGuard.IsIdentifierToken"/>): AMQP <c>is</c>, <c>exchange.name</c>,
/// <c>exchange.type</c>, <c>queue.name</c>; Kafka <c>topic</c>; Kafka <c>groupId</c> as a string
/// or as the members of its <c>enum</c>; AMQP <c>cc</c> routing keys (identifier characters and
/// the wildcards '*' and '#'). Any other value is left out with a
/// <c>binding-value-not-emitted</c> note. <c>const</c>, <c>default</c> and every other field of
/// a binding are never read.
/// </para>
/// Not recognised: a file with two AsyncAPI documents (<c>multiple-documents</c>); references to
/// other files or URLs; a channel or an operation behind <c>$ref</c>; traits; server and message
/// bindings; bindings other than AMQP and Kafka; correlation ids, headers and reply operations;
/// server variables and security.
/// </summary>
internal sealed class AsyncApiExtractor : IExtractor
{
    private static readonly ExtractionRule Document = new("asyncapi.document", 2);
    private static readonly ExtractionRule Channel = new("asyncapi.channel", 2);
    private static readonly ExtractionRule Message = new("asyncapi.message", 2);
    private static readonly ExtractionRule Operation = new("asyncapi.operation", 2);
    private static readonly ExtractionRule Schema = new("asyncapi.schema", 2);

    /// <summary>References that are followed from a message site before it is given up.</summary>
    internal const int MaxReferenceHops = 8;

    public string Id => "asyncapi";
    public IReadOnlyList<ExtractionRule> Rules => [Document, Channel, Message, Operation, Schema];

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
        if (DocumentSet.Single(context, file, document => document.Get("asyncapi") is not null) is not { } root)
            return;
        var version = root.Entry("asyncapi")!;
        var text = version.Value.AsText() ?? "";
        var major = text.StartsWith("2.", StringComparison.Ordinal) ? 2 : text.StartsWith("3.", StringComparison.Ordinal) ? 3 : 0;
        if (major == 0)
        {
            context.Note(NoteKind.Undecided, "unsupported-asyncapi-version", file.Path, version.KeyLine);
            return;
        }
        new Reader(context, file, root).Extract(version, text, major);
    }

    private sealed class Reader(ExtractionContext context, SourceText file, DocNode root)
    {
        private readonly string _contract = "contract:" + file.Path;
        private readonly WorkMeter _meter = context.Meter(file);
        private readonly SchemaBody _bodies = new(context, file);

        public void Extract(DocEntry version, string specVersion, int major)
        {
            var info = root.Map("info");
            var title = ContractSchemas.Title(context, file, info);
            List<string> names = [], protocols = [], urls = [];
            var servers = root.Map("servers")?.Entries ?? [];
            if (!_meter.Charge(servers.Count))
                return;
            foreach (var server in servers.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                if (server.Value.Kind != DocKind.Map || !CredentialGuard.IsIdentifierToken(server.Key))
                    continue;
                names.Add(server.Key);
                if (server.Value.Str("protocol") is { } protocol && CredentialGuard.IsIdentifierToken(protocol))
                    protocols.Add(protocol);
                var address = major == 2
                    ? server.Value.Str("url")
                    : server.Value.Str("host") is { } host ? host + (server.Value.Str("pathname") ?? "") : null;
                if (address is null)
                    continue;
                if (CredentialGuard.SafeUrl(address) is { } safe)
                    urls.Add(safe);
                else
                    context.Note(NoteKind.Undecided, "server-url-not-safe", file.Path, server.KeyLine);
            }
            context.Add(FactKind.Schema, _contract, title ?? file.Path,
                [Document.At(file.Path, version.KeyLine, version.Value.EndLine)],
                [
                    ("format", "asyncapi"), ("specVersion", specVersion),
                    ("title", title), ("apiVersion", info?.Str("version")),
                    ("servers", context.List(names, file.Path, version.KeyLine)),
                    ("protocols", context.List(protocols, file.Path, version.KeyLine, sorted: true)),
                    ("serverUrls", context.List(urls, file.Path, version.KeyLine)),
                ]);

            foreach (var message in root.Map("components")?.Map("messages")?.Entries ?? [])
            {
                if (context.Stopped || !_meter.Charge())
                    return;
                // A component that is only a reference names the message where that one is defined.
                if (message.Value.Kind == DocKind.Map && message.Value.Entry("$ref") is null)
                    AddMessage(message.Value, message.Key, message.Key, message.KeyLine);
            }

            if (major == 3)
                ExtractV3();
            else
                ExtractV2();

            if (!_meter.Exhausted)
                ContractSchemas.Add(context, file, root, _contract, Schema, _bodies);
        }

        private void ExtractV3()
        {
            var channelMessages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var channel in root.Map("channels")?.Entries ?? [])
            {
                if (context.Stopped || !_meter.Charge())
                    return;
                if (channel.Value.Kind != DocKind.Map || channelMessages.ContainsKey(channel.Key))
                    continue;
                if (channel.Value.Entry("$ref") is { } reference)
                {
                    context.Note(NoteKind.Undecided, "unsupported-ref", file.Path, reference.KeyLine);
                    continue;
                }
                var messages = new List<string>();
                foreach (var message in channel.Value.Map("messages")?.Entries ?? [])
                {
                    if (!_meter.Charge())
                        return;
                    var key = message.Value.Kind == DocKind.Map && message.Value.Entry("$ref") is null
                        ? AddMessage(message.Value, $"{channel.Key}/{message.Key}", message.Key, message.KeyLine)
                        : Referenced(message.Value, message.KeyLine);
                    if (key is not null)
                        messages.Add(key);
                }
                channelMessages[channel.Key] = messages;

                var attributes = new List<(string, string?)>
                {
                    ("contract", _contract), ("channelId", channel.Key), ("address", channel.Value.Str("address")),
                    ("messages", context.List(messages, file.Path, channel.KeyLine, sorted: true)),
                };
                AddChannelBindings(attributes, channel.Value.Map("bindings"));
                context.Add(FactKind.Channel, ChannelKey(channel.Key), channel.Key,
                    [Channel.At(file.Path, channel.KeyLine, channel.Value.EndLine)], attributes);
            }

            foreach (var operation in root.Map("operations")?.Entries ?? [])
            {
                if (context.Stopped || !_meter.Charge())
                    return;
                if (operation.Value.Kind != DocKind.Map)
                    continue;
                var action = operation.Value.Str("action");
                if (action is not ("send" or "receive"))
                {
                    context.Note(NoteKind.Undecided, "unknown-asyncapi-action", file.Path, operation.KeyLine);
                    continue;
                }
                var channelId = operation.Value.Map("channel")?.Str("$ref") is { } target
                    && JsonPointer.Segments(target) is { Count: 2 } segments && segments[0] == "channels" ? segments[1] : null;
                if (channelId is null || !channelMessages.TryGetValue(channelId, out var ofChannel))
                {
                    context.Note(NoteKind.Undecided, "unresolved-channel", file.Path, operation.KeyLine);
                    continue;
                }

                var messages = ofChannel;
                if (operation.Value.Seq("messages") is { } listed)
                {
                    messages = [];
                    foreach (var item in listed.Items)
                    {
                        if (!_meter.Charge())
                            return;
                        if (Referenced(item, item.StartLine) is { } key)
                            messages.Add(key);
                    }
                }
                AddOperation(action == "send" ? FactKind.Publication : FactKind.Subscription,
                    operation.Key, operation, action, ChannelKey(channelId), messages);
            }
        }

        private void ExtractV2()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in root.Map("channels")?.Entries ?? [])
            {
                if (context.Stopped || !_meter.Charge())
                    return;
                if (channel.Value.Kind != DocKind.Map || !seen.Add(channel.Key))
                    continue;
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
                        {
                            if (!_meter.Charge())
                                return;
                            var line = ReferenceEquals(alternative, message.Value) ? message.KeyLine : alternative.StartLine;
                            string? key;
                            if (alternative.Kind != DocKind.Map || alternative.Entry("$ref") is not null)
                            {
                                key = Referenced(alternative, line);
                            }
                            else if ((alternative.Str("messageId") ?? alternative.Str("name")) is { } name)
                            {
                                key = AddMessage(alternative, $"{channel.Key}/{name}", name, alternative.StartLine);
                            }
                            else
                            {
                                context.Note(NoteKind.Undecided, "unnamed-message", file.Path, line);
                                key = null;
                            }
                            if (key is not null)
                                messages.Add(key);
                        }
                    }
                    all.AddRange(messages);
                    // 2.x names the action of the other party.
                    AddOperation(action == "subscribe" ? FactKind.Publication : FactKind.Subscription,
                        operation.Value.Str("operationId") ?? $"{channel.Key}:{action}", operation, action, ChannelKey(channel.Key), messages);
                }

                var attributes = new List<(string, string?)>
                {
                    ("contract", _contract), ("address", channel.Key),
                    ("messages", context.List(all, file.Path, channel.KeyLine, sorted: true)),
                };
                AddChannelBindings(attributes, channel.Value.Map("bindings"));
                context.Add(FactKind.Channel, ChannelKey(channel.Key), channel.Key,
                    [Channel.At(file.Path, channel.KeyLine, channel.Value.EndLine)], attributes);
            }
        }

        private string ChannelKey(string channelId) => $"channel:{file.Path}#{channelId}";

        private void AddOperation(FactKind kind, string id, DocEntry operation, string action, string channelKey, List<string> messages)
        {
            var attributes = new List<(string, string?)>
            {
                ("contract", _contract), ("action", action), ("channel", channelKey),
                ("messages", context.List(messages, file.Path, operation.KeyLine, sorted: true)),
                ("summary", operation.Value.Str("summary") is { } summary ? TextUtil.Bounded(summary, 200) : null),
            };
            var bindings = operation.Value.Map("bindings");
            if (bindings?.Map("amqp")?.Entry("cc") is { Value.Kind: DocKind.Seq } cc && _meter.Charge(cc.Value.Items.Count))
            {
                var keys = new List<string>();
                foreach (var item in cc.Value.Items)
                {
                    if (item.AsText() is { } routingKey && IsRoutingKey(routingKey))
                        keys.Add(routingKey);
                    else
                        context.Note(NoteKind.Redacted, "binding-value-not-emitted", file.Path, cc.KeyLine);
                }
                attributes.Add(("amqp.routingKeys", context.List(keys, file.Path, cc.KeyLine)));
            }
            if (bindings?.Map("kafka")?.Entry("groupId") is { } group)
            {
                // A string in old versions of the binding, a schema with an enum in new ones.
                List<string> members = group.Value.AsText() is { } text ? [text]
                    : group.Value.Kind == DocKind.Map && group.Value.Seq("enum") is { } list && _meter.Charge(list.Items.Count)
                        ? list.Items.Select(i => i.AsText() ?? "").ToList()
                        : [];
                if (members.Count > 0 && members.TrueForAll(CredentialGuard.IsIdentifierToken))
                    attributes.Add(("kafka.groupId", context.List(members, file.Path, group.KeyLine)));
                else if (members.Count > 0)
                    context.Note(NoteKind.Redacted, "binding-value-not-emitted", file.Path, group.KeyLine);
            }

            var prefix = kind == FactKind.Publication ? "publication" : "subscription";
            context.Add(kind, $"{prefix}:{file.Path}:{id}", id,
                [Operation.At(file.Path, operation.KeyLine, operation.Value.EndLine)], attributes);
        }

        /// <summary>Adds the message that is written at this node, and returns its key.</summary>
        private string AddMessage(DocNode node, string id, string fallbackName, int line)
        {
            var key = $"message:{file.Path}#{id}";
            var name = node.Str("name");
            var attributes = new List<(string, string?)>
            {
                ("contract", _contract), ("name", name), ("title", node.Str("title")),
                ("contentType", node.Str("contentType") is { } contentType && OpenApiExtractor.IsMediaType(contentType) ? contentType : null),
            };
            // AsyncAPI 3 with a multi-format schema keeps the schema one level down.
            var payload = node.Entry("payload");
            if (payload is { Value.Kind: DocKind.Map } && payload.Value.Get("schemaFormat") is not null && payload.Value.Entry("schema") is { } inner)
                payload = inner;
            if (ContractSchemas.Reference(context, file, root, payload?.Value) is { } reference)
            {
                attributes.Add(("payloadSchema", reference));
                if (reference == "inline")
                    attributes.Add(("payload", _bodies.Write(payload!.Value, payload.KeyLine)));
            }
            context.Add(FactKind.Message, key, name ?? fallbackName, [Message.At(file.Path, line, node.EndLine)], attributes);
            return key;
        }

        /// <summary>
        /// The key of the message that a reference leads to: a component message, or a message
        /// written in a 3.x channel. Null with an <c>unresolved-message</c> note otherwise.
        /// </summary>
        private string? Referenced(DocNode site, int line)
        {
            var node = site;
            for (var hop = 0; hop < MaxReferenceHops; hop++)
            {
                if (node.Kind != DocKind.Map || node.Str("$ref") is not { } reference || !_meter.Charge())
                    break;
                if (JsonPointer.Segments(reference) is not { } segments || JsonPointer.Resolve(root, segments) is not { Kind: DocKind.Map } target)
                    break;
                node = target;
                if (target.Entry("$ref") is not null)
                    continue;
                if (segments is ["components", "messages", var component])
                    return $"message:{file.Path}#{component}";
                if (segments is ["channels", var channel, "messages", var message])
                    return $"message:{file.Path}#{channel}/{message}";
                break;
            }
            context.Note(NoteKind.Undecided, "unresolved-message", file.Path, line);
            return null;
        }

        private void AddChannelBindings(List<(string, string?)> attributes, DocNode? bindings)
        {
            var amqp = bindings?.Map("amqp");
            var kafka = bindings?.Map("kafka");
            if (amqp is not null)
            {
                var role = Token(amqp.Entry("is"));
                attributes.Add(("transport", "amqp"));
                attributes.Add(("amqp.is", role));
                attributes.Add(("kind", role == "queue" ? "queue" : role == "routingKey" ? "exchange" : null));
                attributes.Add(("amqp.exchange", Token(amqp.Map("exchange")?.Entry("name"))));
                attributes.Add(("amqp.exchange.type", Token(amqp.Map("exchange")?.Entry("type"))));
                attributes.Add(("amqp.queue", Token(amqp.Map("queue")?.Entry("name"))));
            }
            else if (kafka is not null)
            {
                attributes.Add(("transport", "kafka"));
                attributes.Add(("kind", "topic"));
                attributes.Add(("kafka.topic", Token(kafka.Entry("topic"))));
            }
        }

        /// <summary>The text of a binding field when it is an identifier token; otherwise null, with a note when there is a text.</summary>
        private string? Token(DocEntry? entry)
        {
            if (entry?.Value.AsText() is not { Length: > 0 } text)
                return null;
            if (CredentialGuard.IsIdentifierToken(text))
                return text;
            context.Note(NoteKind.Redacted, "binding-value-not-emitted", file.Path, entry.KeyLine);
            return null;
        }

        /// <summary>An AMQP routing key or pattern: identifier characters and the wildcards '*' and '#', at most 100 characters.</summary>
        private static bool IsRoutingKey(string value)
        {
            if (value.Length is 0 or > 100)
                return false;
            foreach (var c in value)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '*' or '#'))
                    return false;
            return true;
        }
    }
}
