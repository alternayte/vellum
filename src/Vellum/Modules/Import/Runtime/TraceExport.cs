using System.Globalization;
using System.Text;
using System.Text.Json;
using Vellum.Modules.Import.Proposals;

namespace Vellum.Modules.Import.Runtime;

/// <summary>The bounds of one trace upload. A file over any of them is refused as a whole: a cut trace export would read like a complete one.</summary>
/// <param name="MaxBytes">The size of the upload.</param>
/// <param name="MaxSpans">Spans across the file.</param>
/// <param name="MaxServices">Distinct services, peers included.</param>
/// <param name="MaxEdges">Distinct edges.</param>
/// <param name="MaxTokenBytes">One JSON token, such as one string value.</param>
internal sealed record TraceLimits(long MaxBytes, int MaxSpans, int MaxServices, int MaxEdges, int MaxTokenBytes = 1024 * 1024)
{
    public const int MaxDepth = 64;
    public const int MaxOperationsPerEdge = 50;
    /// <summary>Links (OTLP) or references (Jaeger) of one span. A span with more is refused with the file.</summary>
    public const int MaxLinksPerSpan = 128;
    public static readonly TraceLimits Default = new(256L * 1024 * 1024, 1_000_000, 2_000, 10_000);
}

/// <summary>Why an upload was refused. <see cref="TooLarge"/> answers 413; every other refusal 400.</summary>
internal sealed class TraceRefusal(string code, string message, bool tooLarge = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool TooLarge { get; } = tooLarge;
}

/// <summary>
/// Reads a trace export and derives the services and the edges between them. It reads the
/// JSON as a stream of tokens and keeps, per span, only what the derivation needs: the trace
/// and span IDs, the parent, the links, the service, the operation name, the span kind, the
/// peer (<c>peer.service</c>, else <c>server.address</c>), whether the status is an error,
/// and the start and end time. Every other attribute, every event and every resource
/// attribute other than <c>service.name</c> is never held. When the read ends, only the
/// derived <see cref="RuntimeGraph"/> is left.
/// <list type="bullet">
/// <item><b>OTLP JSON</b> (<c>ExportTraceServiceRequest</c>): <c>resourceSpans[].scopeSpans[].spans[]</c>.</item>
/// <item><b>Tempo</b>: the same shape under <c>batches</c>, or <c>trace.resourceSpans</c>; <c>instrumentationLibrarySpans</c> is read like <c>scopeSpans</c>.</item>
/// <item><b>Jaeger</b> ("Download JSON" and the query API): <c>data[].spans[]</c> with <c>data[].processes</c>; the kind and the peer come from tags.</item>
/// </list>
/// <para>
/// A call edge is a CLIENT span with a SERVER child in another service; a CLIENT span with no
/// such child is a call to its peer. A messaging edge is a PRODUCER span that a CONSUMER span
/// of another service links to or has as its parent. Spans of one service never make an edge.
/// </para>
/// </summary>
internal sealed class TraceExport
{
    private enum Kind : byte { Unspecified, Internal, Server, Client, Producer, Consumer }

    private sealed class Span
    {
        public required string Trace;
        public required string Id;
        public string? Parent;
        public string? Service;
        public string? ProcessId;
        public string? Operation;
        public Kind Kind;
        public string? Peer;
        public bool Error;
        public long Start = long.MaxValue;
        public long End = long.MinValue;
        public List<(string Trace, string Id)>? Links;
    }

    private readonly JsonPull _json;
    private readonly TraceLimits _limits;
    private readonly CancellationToken _ct;
    private readonly List<Span> _spans = [];
    private readonly Dictionary<string, string> _interned = new(StringComparer.Ordinal);
    private string? _format;
    private int _redacted;
    /// <summary>Every span read so far, kept or not. The bound applies to it as each span is read.</summary>
    private int _read;

    private TraceExport(Stream body, TraceLimits limits, CancellationToken ct)
    {
        _json = new JsonPull(body, limits.MaxBytes, limits.MaxTokenBytes);
        _limits = limits;
        _ct = ct;
    }

    /// <summary>Reads the export and derives its graph. Throws <see cref="TraceRefusal"/> for a file that is malformed, of no known shape, or over a bound.</summary>
    /// <param name="fileName">The name the file is cited under. It is not read.</param>
    public static async Task<RuntimeGraph> ReadAsync(Stream body, string fileName, TraceLimits limits, CancellationToken ct)
    {
        var export = new TraceExport(body, limits, ct);
        try
        {
            await export.NextAsync();
            await export.RootAsync();
            if (await export._json.ReadAsync(ct))
                throw Malformed();
        }
        catch (JsonException)
        {
            throw Malformed();
        }
        catch (InvalidOperationException)
        {
            // A string that is not valid UTF-8 or holds a lone surrogate. Its bytes are not logged.
            throw Malformed();
        }
        if (export._format is null)
            throw new TraceRefusal("trace_format_unknown",
                "The file is JSON, and it is no trace export Vellum reads: an OTLP JSON export (resourceSpans), a Tempo export (batches or trace.resourceSpans) or a Jaeger export (data[].spans).");
        if (export._spans.Count == 0)
            throw new TraceRefusal("trace_empty", "The file holds no span with a service, so it shows no edge.");
        return export.Derive(fileName);
    }

    private static TraceRefusal Malformed() =>
        new("trace_malformed", "The file is not well-formed JSON, or a value has the wrong type. Export the traces again.");

    private async ValueTask NextAsync()
    {
        if (!await _json.ReadAsync(_ct))
            throw Malformed();
    }

    /// <summary>Leaves the current value: a scalar is done; an object or an array is read to its end.</summary>
    private async ValueTask SkipAsync()
    {
        if (_json.Token is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
            return;
        var depth = 1;
        while (depth > 0)
        {
            await NextAsync();
            if (_json.Token is JsonTokenType.StartObject or JsonTokenType.StartArray)
                depth++;
            else if (_json.Token is JsonTokenType.EndObject or JsonTokenType.EndArray)
                depth--;
        }
    }

    /// <summary>Reads the properties of the current object. Each handler reads its value to its last token. A value that is no object is left.</summary>
    private async ValueTask ObjectAsync(Func<string, ValueTask> property)
    {
        if (_json.Token != JsonTokenType.StartObject)
        {
            await SkipAsync();
            return;
        }
        while (true)
        {
            await NextAsync();
            if (_json.Token == JsonTokenType.EndObject)
                return;
            var name = _json.Text ?? "";
            await NextAsync();
            await property(name);
        }
    }

    private async ValueTask ArrayAsync(Func<ValueTask> item)
    {
        if (_json.Token != JsonTokenType.StartArray)
        {
            await SkipAsync();
            return;
        }
        while (true)
        {
            await NextAsync();
            if (_json.Token == JsonTokenType.EndArray)
                return;
            await item();
        }
    }

    /// <summary>The current value as text when it is a string, a number or a boolean; null otherwise.</summary>
    private async ValueTask<string?> ScalarAsync()
    {
        switch (_json.Token)
        {
            case JsonTokenType.String or JsonTokenType.Number:
                return _json.Text;
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            default:
                await SkipAsync();
                return null;
        }
    }

    private ValueTask RootAsync() => ObjectAsync(name => name switch
    {
        "resourceSpans" => Format("otlp", () => ArrayAsync(ResourceSpansAsync)),
        "batches" => Format("tempo", () => ArrayAsync(ResourceSpansAsync)),
        "trace" => Format("tempo", RootAsync),
        "data" => Format("jaeger", () => ArrayAsync(JaegerTraceAsync)),
        _ => SkipAsync(),
    });

    private ValueTask Format(string format, Func<ValueTask> read)
    {
        // An enclosing "trace" names Tempo; what it holds is the same OTLP shape.
        _format ??= format;
        return read();
    }

    private async ValueTask ResourceSpansAsync()
    {
        string? service = null;
        var spans = new List<Span>();
        await ObjectAsync(name => name switch
        {
            "resource" => ObjectAsync(field => field == "attributes"
                ? ArrayAsync(async () =>
                {
                    var (key, value) = await KeyValueAsync();
                    if (key == "service.name")
                        service = value;
                })
                : SkipAsync()),
            "scopeSpans" or "instrumentationLibrarySpans" => ArrayAsync(() => ObjectAsync(field => field == "spans"
                ? ArrayAsync(async () => Hold(spans, await OtlpSpanAsync()))
                : SkipAsync())),
            _ => SkipAsync(),
        });
        // The resource can follow its spans in the object, so the service is given at the end.
        Keep(spans, service ?? "unknown_service");
    }

    /// <summary>An OTLP attribute: its key, and its value when that is a string.</summary>
    private async ValueTask<(string? Key, string? Value)> KeyValueAsync()
    {
        string? key = null, value = null;
        await ObjectAsync(async name =>
        {
            if (name == "key")
                key = await ScalarAsync();
            else if (name == "value")
                await ObjectAsync(async type => value = type == "stringValue" ? await ScalarAsync() : await SkipThenNull());
            else
                await SkipAsync();
        });
        return (key, value);
    }

    private async ValueTask<string?> SkipThenNull()
    {
        await SkipAsync();
        return null;
    }

    private async ValueTask<Span> OtlpSpanAsync()
    {
        string? trace = null, id = null, parent = null, operation = null, peerService = null, serverAddress = null;
        var kind = Kind.Unspecified;
        var error = false;
        long start = long.MaxValue, end = long.MinValue;
        List<(string, string)>? links = null;
        await ObjectAsync(async name =>
        {
            switch (name)
            {
                case "traceId": trace = await ScalarAsync(); break;
                case "spanId": id = await ScalarAsync(); break;
                case "parentSpanId": parent = await ScalarAsync(); break;
                case "name": operation = await ScalarAsync(); break;
                case "kind": kind = OtlpKind(await ScalarAsync()); break;
                case "startTimeUnixNano": start = Micros(await ScalarAsync(), nanos: true) ?? start; break;
                case "endTimeUnixNano": end = Micros(await ScalarAsync(), nanos: true) ?? end; break;
                case "status":
                    await ObjectAsync(async field =>
                    {
                        if (field == "code")
                            error = await ScalarAsync() is "2" or "STATUS_CODE_ERROR";
                        else
                            await SkipAsync();
                    });
                    break;
                case "attributes":
                    await ArrayAsync(async () =>
                    {
                        var (key, value) = await KeyValueAsync();
                        if (key == "peer.service")
                            peerService = value;
                        else if (key == "server.address")
                            serverAddress = value;
                    });
                    break;
                case "links":
                    await ArrayAsync(async () =>
                    {
                        string? linkTrace = null, linkSpan = null;
                        await ObjectAsync(async field =>
                        {
                            if (field == "traceId") linkTrace = await ScalarAsync();
                            else if (field == "spanId") linkSpan = await ScalarAsync();
                            else await SkipAsync();
                        });
                        if (linkTrace is not null && linkSpan is not null)
                            AddLink(ref links, linkTrace, linkSpan);
                    });
                    break;
                default: await SkipAsync(); break;
            }
        });
        return new Span
        {
            Trace = Intern(trace ?? ""), Id = Id(id ?? ""), Parent = string.IsNullOrEmpty(parent) ? null : Id(parent),
            Operation = operation, Kind = kind, Peer = peerService ?? serverAddress, Error = error, Start = start, End = end, Links = links,
        };
    }

    private async ValueTask JaegerTraceAsync()
    {
        var spans = new List<Span>();
        var processes = new Dictionary<string, string?>(StringComparer.Ordinal);
        await ObjectAsync(name => name switch
        {
            "spans" => ArrayAsync(async () => Hold(spans, await JaegerSpanAsync())),
            "processes" => ObjectAsync(async processId =>
            {
                string? service = null;
                await ObjectAsync(async field => service = field == "serviceName" ? await ScalarAsync() : await SkipThenNull() ?? service);
                processes[processId] = service;
            }),
            _ => SkipAsync(),
        });
        // The processes can follow the spans in the trace.
        foreach (var group in spans.GroupBy(s => s.Service ?? (s.ProcessId is { } pid ? processes.GetValueOrDefault(pid) : null) ?? "unknown_service"))
            Keep([.. group], group.Key);
    }

    private async ValueTask<Span> JaegerSpanAsync()
    {
        string? trace = null, id = null, parent = null, operation = null, processId = null, service = null;
        string? peerService = null, serverAddress = null;
        var kind = Kind.Unspecified;
        var error = false;
        long? start = null, duration = null;
        List<(string, string)>? links = null;
        await ObjectAsync(async name =>
        {
            switch (name)
            {
                case "traceID": trace = await ScalarAsync(); break;
                case "spanID": id = await ScalarAsync(); break;
                case "parentSpanID": parent = await ScalarAsync(); break;
                case "operationName": operation = await ScalarAsync(); break;
                case "processID": processId = await ScalarAsync(); break;
                case "startTime": start = Micros(await ScalarAsync(), nanos: false); break;
                case "duration": duration = Micros(await ScalarAsync(), nanos: false); break;
                case "process":
                    await ObjectAsync(async field => service = field == "serviceName" ? await ScalarAsync() : await SkipThenNull() ?? service);
                    break;
                case "references":
                    await ArrayAsync(async () =>
                    {
                        string? type = null, refTrace = null, refSpan = null;
                        await ObjectAsync(async field =>
                        {
                            if (field == "refType") type = await ScalarAsync();
                            else if (field == "traceID") refTrace = await ScalarAsync();
                            else if (field == "spanID") refSpan = await ScalarAsync();
                            else await SkipAsync();
                        });
                        if (refSpan is null)
                            return;
                        // CHILD_OF is the parent; FOLLOWS_FROM is how a consumer points at its producer.
                        if (type == "CHILD_OF" && (refTrace is null || refTrace == trace))
                            parent ??= refSpan;
                        else
                            AddLink(ref links, refTrace ?? trace ?? "", refSpan);
                    });
                    break;
                case "tags":
                    await ArrayAsync(async () =>
                    {
                        string? key = null, value = null;
                        await ObjectAsync(async field =>
                        {
                            if (field == "key") key = await ScalarAsync();
                            else if (field == "value") value = await ScalarAsync();
                            else await SkipAsync();
                        });
                        switch (key)
                        {
                            case "span.kind": kind = JaegerKind(value); break;
                            case "peer.service": peerService = value; break;
                            case "server.address": serverAddress = value; break;
                            case "error": error |= value == "true"; break;
                            case "otel.status_code": error |= value == "ERROR"; break;
                        }
                    });
                    break;
                default: await SkipAsync(); break;
            }
        });
        return new Span
        {
            Trace = Intern(trace ?? ""), Id = Id(id ?? ""), Parent = string.IsNullOrEmpty(parent) ? null : Id(parent),
            Service = service, ProcessId = processId, Operation = operation, Kind = kind, Peer = peerService ?? serverAddress, Error = error,
            Start = start ?? long.MaxValue, End = start is { } s ? s + (duration ?? 0) : long.MinValue, Links = links,
        };
    }

    /// <summary>
    /// Counts a span the moment it is read, whether it is kept or not, and refuses the file at
    /// the bound. A span without an ID can be no end of an edge, so it is dropped at once; its
    /// service is not known yet, so the others wait in <paramref name="pending"/>.
    /// </summary>
    private void Hold(List<Span> pending, Span span)
    {
        if (++_read > _limits.MaxSpans)
            throw new TraceRefusal("trace_too_many_spans",
                $"The file holds more than {_limits.MaxSpans} spans. Export a shorter window.", tooLarge: true);
        if (span.Id.Length > 0)
            pending.Add(span);
    }

    private void AddLink(ref List<(string, string)>? links, string trace, string id)
    {
        links ??= [];
        if (links.Count >= TraceLimits.MaxLinksPerSpan)
            throw new TraceRefusal("trace_too_many_links",
                $"A span of the file has more than {TraceLimits.MaxLinksPerSpan} links. Vellum reads no such span.", tooLarge: true);
        links.Add((Intern(trace), Id(id)));
    }

    /// <summary>Gives the spans their service and keeps them. A service name that is not fit to keep drops its spans.</summary>
    private void Keep(List<Span> spans, string service)
    {
        var name = RuntimeGraph.CleanName(service);
        if (name is null)
        {
            _redacted += spans.Count;
            return;
        }
        foreach (var span in spans)
        {
            span.Service = Intern(name);
            _spans.Add(span);
        }
    }

    private string Intern(string value)
    {
        if (_interned.TryGetValue(value, out var known))
            return known;
        _interned[value] = value;
        return value;
    }

    private static string Id(string value) => value;

    private static Kind OtlpKind(string? value) => value switch
    {
        "1" or "SPAN_KIND_INTERNAL" => Kind.Internal,
        "2" or "SPAN_KIND_SERVER" => Kind.Server,
        "3" or "SPAN_KIND_CLIENT" => Kind.Client,
        "4" or "SPAN_KIND_PRODUCER" => Kind.Producer,
        "5" or "SPAN_KIND_CONSUMER" => Kind.Consumer,
        _ => Kind.Unspecified,
    };

    private static Kind JaegerKind(string? value) => value?.ToLowerInvariant() switch
    {
        "internal" => Kind.Internal,
        "server" => Kind.Server,
        "client" => Kind.Client,
        "producer" => Kind.Producer,
        "consumer" => Kind.Consumer,
        _ => Kind.Unspecified,
    };

    /// <summary>A time in microseconds since the epoch: OTLP gives nanoseconds as a string or a number, Jaeger microseconds.</summary>
    private static long? Micros(string? value, bool nanos) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? nanos ? number / 1000 : number
            : null;

    private sealed class EdgeTotals
    {
        public required string From;
        public required string To;
        public required bool Messaging;
        public long Calls;
        public long Errors;
        public long First = long.MaxValue;
        public long Last = long.MinValue;
        public readonly SortedSet<string> Operations = new(StringComparer.Ordinal);
        public int OperationsLeftOut;
    }

    private RuntimeGraph Derive(string fileName)
    {
        var byId = new Dictionary<(string, string), Span>();
        foreach (var span in _spans)
            byId.TryAdd((span.Trace, span.Id), span);

        var edges = new Dictionary<(string, string, bool), EdgeTotals>();
        var peers = new SortedSet<string>(StringComparer.Ordinal);
        var answered = new HashSet<Span>();
        var unmatchedProducers = 0;
        var redactedOperations = 0;

        void Add(string from, string to, bool messaging, Span at, string? operation, bool error)
        {
            if (from == to)
                return;
            if (!edges.TryGetValue((from, to, messaging), out var totals))
            {
                if (edges.Count >= _limits.MaxEdges)
                    throw new TraceRefusal("trace_too_many_edges", $"The file shows more than {_limits.MaxEdges} edges. Export fewer services.", tooLarge: true);
                edges[(from, to, messaging)] = totals = new EdgeTotals { From = from, To = to, Messaging = messaging };
            }
            totals.Calls++;
            if (error)
                totals.Errors++;
            if (at.Start != long.MaxValue)
                totals.First = Math.Min(totals.First, at.Start);
            if (at.End != long.MinValue)
                totals.Last = Math.Max(totals.Last, at.End);
            var name = operation is null ? null : RuntimeGraph.CleanName(operation);
            if (operation is not null && name is null)
                redactedOperations++;
            if (name is not null && !totals.Operations.Contains(name))
            {
                if (totals.Operations.Count < TraceLimits.MaxOperationsPerEdge)
                    totals.Operations.Add(name);
                else
                    totals.OperationsLeftOut++;
            }
        }

        // A SERVER span whose parent is a CLIENT span of another service: the call reached it.
        foreach (var span in _spans)
        {
            if (span.Kind == Kind.Server && span.Parent is { } parentId && byId.TryGetValue((span.Trace, parentId), out var parent)
                && parent.Kind == Kind.Client && parent.Service != span.Service)
            {
                answered.Add(parent);
                Add(parent.Service!, span.Service!, messaging: false, span, span.Operation ?? parent.Operation, span.Error || parent.Error);
            }
        }

        // A CLIENT span that no SERVER span answers: the call went to its peer.
        foreach (var span in _spans.Where(s => s.Kind == Kind.Client && !answered.Contains(s)))
        {
            if (RuntimeGraph.CleanName(span.Peer) is not { } peer || peer == span.Service)
                continue;
            peers.Add(Intern(peer));
            Add(span.Service!, peer, messaging: false, span, span.Operation, span.Error);
        }

        // A CONSUMER span that links to a PRODUCER span, or has one as its parent.
        var consumed = new HashSet<Span>();
        foreach (var span in _spans.Where(s => s.Kind == Kind.Consumer))
        {
            var sources = (span.Links ?? []).AsEnumerable();
            if (span.Parent is { } parentId)
                sources = sources.Append((span.Trace, parentId));
            foreach (var (trace, id) in sources)
            {
                if (byId.TryGetValue((trace, id), out var producer) && producer.Kind == Kind.Producer && producer.Service != span.Service)
                {
                    consumed.Add(producer);
                    Add(producer.Service!, span.Service!, messaging: true, span, producer.Operation, span.Error || producer.Error);
                }
            }
        }
        unmatchedProducers = _spans.Count(s => s.Kind == Kind.Producer && !consumed.Contains(s));

        var serviceNames = _spans.Select(s => s.Service!).Concat(peers).Distinct(StringComparer.Ordinal).ToList();
        if (serviceNames.Count > _limits.MaxServices)
            throw new TraceRefusal("trace_too_many_services", $"The file shows more than {_limits.MaxServices} services. Export fewer services.", tooLarge: true);
        var instrumented = _spans.Select(s => s.Service!).ToHashSet(StringComparer.Ordinal);

        var services = serviceNames.Select(name => new RuntimeService(ServiceKey(name), name, "measured",
            new Dictionary<string, string> { ["seenAs"] = instrumented.Contains(name) ? "instrumented" : "peer" })).ToList();
        var runtimeEdges = edges.Values.Select(e =>
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["via"] = e.Messaging ? "messaging" : "call",
                ["operations"] = JsonSerializer.Serialize(e.Operations),
                [FactMapping.SamplePrefix + "calls"] = RuntimeGraph.Number(e.Calls),
                [FactMapping.SamplePrefix + "errors"] = RuntimeGraph.Number(e.Errors),
            };
            if (e.OperationsLeftOut > 0)
                attributes[FactMapping.SamplePrefix + "operationsLeftOut"] = RuntimeGraph.Number(e.OperationsLeftOut);
            if (e.First != long.MaxValue)
                attributes[FactMapping.SamplePrefix + "firstSeen"] = Time(e.First);
            if (e.Last != long.MinValue)
                attributes[FactMapping.SamplePrefix + "lastSeen"] = Time(e.Last);
            return new RuntimeEdge(
                $"{(e.Messaging ? "messaging" : "call")}:{e.From}->{e.To}", ServiceKey(e.From), ServiceKey(e.To),
                e.Messaging ? "sends messages to" : "calls", "measured", attributes);
        }).ToList();

        var starts = _spans.Where(s => s.Start != long.MaxValue).Select(s => s.Start).DefaultIfEmpty(long.MaxValue).Min();
        var ends = _spans.Where(s => s.End != long.MinValue).Select(s => s.End).DefaultIfEmpty(long.MinValue).Max();
        var header = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = "traces",
            ["file"] = fileName,
            ["exportFormat"] = _format!,
            ["spans"] = RuntimeGraph.Number(_spans.Count),
            ["window"] = starts == long.MaxValue || ends == long.MinValue ? "unknown" : $"{Time(starts)}/{Time(ends)}",
        };
        if (_redacted > 0)
            header["spansDropped"] = RuntimeGraph.Number(_redacted);
        if (redactedOperations > 0)
            header["operationsRedacted"] = RuntimeGraph.Number(redactedOperations);
        if (unmatchedProducers > 0)
            header["producersWithoutConsumer"] = RuntimeGraph.Number(unmatchedProducers);
        return new RuntimeGraph(header, services, runtimeEdges, IsComplete: true);
    }

    public static string ServiceKey(string name) => "service:" + name;

    private static string Time(long micros) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(micros / 1000, 0, 253402300799999)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The tokens of a JSON stream, one at a time, with no document in memory. It holds a buffer
/// of the bytes not read yet and the reader's state between buffers. A token larger than
/// <c>maxTokenBytes</c> and a stream longer than <c>maxBytes</c> are refused.
/// </summary>
internal sealed class JsonPull(Stream stream, long maxBytes, int maxTokenBytes)
{
    /// <summary>A string longer than this is read as null: no name the graph keeps is near it.</summary>
    private const int MaxKeptString = 4096;

    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _final;
    private long _read;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = TraceLimits.MaxDepth });

    public JsonTokenType Token { get; private set; }

    /// <summary>The text of a property name, a string or a number token. Null for other tokens and for a long string.</summary>
    public string? Text { get; private set; }

    /// <exception cref="JsonException">The bytes are not JSON, or nest deeper than <see cref="TraceLimits.MaxDepth"/>.</exception>
    public async ValueTask<bool> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryRead())
                return true;
            if (_final)
            {
                if (_start != _end)
                    throw new JsonException("Trailing bytes.");
                return false;
            }
            await FillAsync(ct);
        }
    }

    private bool TryRead()
    {
        var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _final, _state);
        if (!reader.Read())
        {
            // White space at the end of the stream is no token.
            if (_final)
                _start += (int)reader.BytesConsumed;
            return false;
        }
        Token = reader.TokenType;
        Text = Token switch
        {
            JsonTokenType.PropertyName or JsonTokenType.String => reader.ValueSpan.Length > MaxKeptString ? null : reader.GetString(),
            JsonTokenType.Number => Encoding.ASCII.GetString(reader.ValueSpan),
            _ => null,
        };
        _start += (int)reader.BytesConsumed;
        _state = reader.CurrentState;
        return true;
    }

    private async ValueTask FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= maxTokenBytes)
                throw new TraceRefusal("trace_token_too_large", $"One value of the file is larger than {maxTokenBytes} bytes. Vellum reads no such value.", tooLarge: true);
            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, maxTokenBytes));
        }
        var count = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
        if (count == 0)
        {
            _final = true;
            return;
        }
        _read += count;
        if (_read > maxBytes)
            throw new TraceRefusal("trace_too_large", $"The file is larger than {maxBytes} bytes. Export a shorter window.", tooLarge: true);
        _end += count;
    }
}
