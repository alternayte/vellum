using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Vellum.Modules.Ai;

namespace Vellum.Shared.Telemetry;

/// <summary>
/// The last step before a span leaves the process. The instrumentation libraries already
/// leave out headers, bodies and query values; this step does not depend on that.
/// <list type="bullet">
/// <item>The query string of a request is removed.</item>
/// <item>The address of an outbound request is cut to its origin (scheme, host and port): a
/// path can name a repository, a file or a model.</item>
/// <item>Every other text value goes through <see cref="SecretRedactor"/>.</item>
/// <item>A database span with no parent is not exported: it is the polling of the event store
/// and of the job tables, several each second. The database metrics still count it.</item>
/// </list>
/// </summary>
public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    private const string NpgsqlSource = "Npgsql";

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name == NpgsqlSource && activity.ParentSpanId == default)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        foreach (var (key, value) in activity.TagObjects.ToArray())
        {
            if (key is "url.query" or "http.target" or "http.url")
                activity.SetTag(key, null);
            else if (key == "url.full" && value is string url)
                activity.SetTag(key, TelemetryText.OriginOf(url));
            else if (TelemetryText.Clean(value) is { } cleaned)
                activity.SetTag(key, cleaned);
        }

        // The display name of a server span is the method and the route template. A request
        // that matches no route has its raw path there.
        var name = TelemetryText.Redact(activity.DisplayName);
        if (!ReferenceEquals(name, activity.DisplayName))
            activity.DisplayName = name;
    }
}

/// <summary>
/// The last step before a log record leaves the process: the message and every text
/// attribute go through <see cref="SecretRedactor"/>. An exception is replaced by its type,
/// its redacted message and its stack trace, so the text of the exception cannot carry a
/// value past the redactor.
/// </summary>
public sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        var attributes = new List<KeyValuePair<string, object?>>((record.Attributes?.Count ?? 0) + 3);
        foreach (var (key, value) in record.Attributes ?? [])
        {
            // The request log of the framework has the query string as a value of its own.
            attributes.Add(key == "QueryString" && value is string { Length: > 0 }
                ? new(key, "?[query]")
                : new(key, TelemetryText.Clean(value) ?? value));
        }

        if (record.Exception is { } exception)
        {
            attributes.Add(new("exception.type", exception.GetType().FullName));
            attributes.Add(new("exception.message", TelemetryText.Redact(exception.Message)));
            if (exception.StackTrace is { } stack)
                attributes.Add(new("exception.stacktrace", TelemetryText.Redact(stack)));
            record.Exception = null;
        }

        record.Attributes = attributes;
        if (record.FormattedMessage is { } message)
            record.FormattedMessage = TelemetryText.Redact(message);
        if (record.Body is { } body)
            record.Body = TelemetryText.Redact(body);
    }
}

internal static partial class TelemetryText
{
    /// <summary>
    /// <see cref="SecretRedactor"/>, and before it the query string of every URL and of every
    /// path is cut off: a query value has no shape that the redactor knows (an OAuth code, a
    /// search text), and no reader of telemetry needs it.
    /// </summary>
    public static string Redact(string text) =>
        string.IsNullOrEmpty(text) ? text : SecretRedactor.Redact(Query().Replace(text, "$1?[query]"));

    // A URL, or a path that starts with "/", up to its "?", and then the query to the next space or quote.
    [GeneratedRegex(@"((?:[A-Za-z][A-Za-z0-9+.-]*://|/)[^\s?""'<>]*)\?[^\s""'<>]*", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex Query();

    /// <returns>The redacted text of <paramref name="value"/>, or null when the value is a number, a flag or null and stays as it is.</returns>
    public static object? Clean(object? value) => value switch
    {
        null or bool or byte or short or int or long or float or double or decimal => null,
        string text => TelemetryText.Redact(text),
        string[] texts => Array.ConvertAll(texts, SecretRedactor.Redact),
        Array => null,
        // A value of another type is written with ToString by the exporter, so it is text.
        _ => TelemetryText.Redact(value.ToString() ?? ""),
    };

    /// <summary>Scheme, host and port. Text that is not an absolute URL gives a fixed word.</summary>
    public static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "", StringComparison.Ordinal) : "[address]";
}
