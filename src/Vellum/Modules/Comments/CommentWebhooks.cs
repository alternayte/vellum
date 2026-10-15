using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Refit;
using Vellum.Modules.Settings;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Modules.Comments;

/// <summary>
/// The workspace webhook (<c>notifications.webhookUrl</c>). One POST per event, with a JSON
/// body of IDs, names and a link. When the workspace has the signing secret
/// <c>notifications:webhookSecret</c>, <c>X-Vellum-Signature</c> is <c>sha256=</c> and the
/// hex HMAC-SHA-256 of <c>{X-Vellum-Timestamp}.{body}</c> under that secret.
/// </summary>
internal interface ICommentWebhookApi
{
    /// <summary>Posts to the address in <see cref="OutboundCall.AbsoluteUri"/>: the webhook URL as it is saved.</summary>
    [Post("/")]
    Task<HttpResponseMessage> Send(
        [Body] HttpContent body,
        [Header("X-Vellum-Event")] string eventName,
        [Header("X-Vellum-Delivery")] string deliveryId,
        [Header("X-Vellum-Timestamp")] string timestamp,
        [Header("X-Vellum-Signature")] string? signature,
        [Property(OutboundCall.PropertyName)] OutboundCall call, CancellationToken ct);
}

/// <summary>
/// Writes webhook events into the outbox in the transaction of the change they report. The
/// worker sends them after the commit, so an event is never sent for a change that rolled
/// back, and a restart does not lose one.
/// </summary>
internal static class CommentWebhooks
{
    public const string Created = "comment.created";
    public const string Resolved = "comment.resolved";
    public const string Mentioned = "comment.mentioned";

    /// <summary>
    /// Adds one event when the workspace has a webhook URL that the instance policy allows.
    /// The payload names the thread, the comment, the person, the target and a link (null when
    /// <see cref="Bootstrap.LinkBase"/> gives no trusted address). It holds
    /// no comment text and no source text.
    /// </summary>
    public static async Task EnqueueAsync(
        HttpContext http, ThreadEntity thread, CommentEntity? comment, string eventName, DateTimeOffset now, CancellationToken ct,
        IReadOnlyList<string>? mentioned = null)
    {
        var services = http.RequestServices;
        var settings = await services.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(thread.WorkspaceId, ct);
        if (settings.WebhookUrl is not SettingsUrl.Usable)
            return;

        var db = services.GetRequiredService<AppDbContext>();
        var actor = eventName == Resolved ? thread.ResolvedBy! : comment!.Author;
        var names = await ThreadReads.NamesAsync(db, mentioned is null ? [actor] : [actor, .. mentioned], ct);
        var deliveryId = Guid.NewGuid();
        var linkBase = services.GetRequiredService<Bootstrap>().LinkBase(http.Request);
        var link = linkBase is null ? null : $"{linkBase}/projects/{thread.ProjectId}?thread={thread.Id}";

        var payload = new JsonObject
        {
            ["event"] = eventName,
            ["deliveryId"] = deliveryId,
            ["occurredAt"] = now,
            ["workspaceId"] = thread.WorkspaceId,
            ["projectId"] = thread.ProjectId,
            ["draftId"] = thread.DraftId,
            ["threadId"] = thread.Id,
            ["commentId"] = comment?.Id,
            ["author"] = Person(actor, names),
            ["target"] = new JsonObject
            {
                ["kind"] = EnumText.Of(thread.TargetKind),
                ["id"] = thread.TargetId,
                ["stepId"] = thread.StepId,
                ["name"] = thread.TargetName,
            },
            ["link"] = link,
        };
        if (mentioned is not null)
            payload["mentioned"] = new JsonArray([.. mentioned.Select(m => (JsonNode)Person(m, names))]);

        db.WebhookDeliveries.Add(new DeliveryEntity
        {
            Id = deliveryId, WorkspaceId = thread.WorkspaceId, ProjectId = thread.ProjectId, Event = eventName,
            Payload = payload.ToJsonString(), CreatedAt = now, NextAttemptAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Tells the worker that an event waits. Called after the commit.</summary>
    public static void Wake(HttpContext http) => http.RequestServices.GetRequiredService<WebhookSignal>().Notify();

    private static JsonObject Person(string userId, IReadOnlyDictionary<string, string> names) => new()
    {
        ["userId"] = userId,
        ["name"] = names.GetValueOrDefault(userId),
    };
}

/// <summary>
/// Wakes the webhook worker when an event is written. A wake that comes while the worker is
/// busy is kept, so the worker looks again at once instead of after the poll interval.
/// </summary>
public sealed class WebhookSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is pending already.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Sends the events of the outbox. An event is taken with a lease, so two app instances never
/// send it at the same time; a send that a crash cuts short is sent again when the lease ends.
/// A 2xx answer delivers it. Any other answer or a transport failure tries again later with a
/// doubling wait, up to <see cref="MaxAttempts"/> sends.
/// </summary>
public sealed class WebhookSender(IServiceScopeFactory scopes, TimeProvider time)
{
    public const int MaxAttempts = 6;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <returns>True when an event was taken, so the caller looks again at once.</returns>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var leaseEnd = now + Lease;

        var taken = await db.Database.SqlQuery<Guid>($"""
            UPDATE comments.webhook_deliveries SET next_attempt_at = {leaseEnd}, attempts = attempts + 1
            WHERE id = (
                SELECT id FROM comments.webhook_deliveries
                WHERE status = 'pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (taken.Count == 0)
        {
            var cutoff = now - Keep;
            await db.WebhookDeliveries.Where(d => d.Status != DeliveryStatus.Pending && d.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
            return false;
        }

        var delivery = await db.WebhookDeliveries.FirstAsync(d => d.Id == taken[0], ct);
        var (sent, result) = await SendAsync(services, delivery, ct);
        delivery.LastResult = result;
        if (sent)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.SentAt = time.GetUtcNow();
        }
        else if (delivery.Attempts >= MaxAttempts || result is "not_configured" or "secret_saved_for_another_host" or "secret_unavailable" or "url_refused" or "invalid_request")
        {
            delivery.Status = DeliveryStatus.Failed;
        }
        else
        {
            delivery.NextAttemptAt = time.GetUtcNow() + FirstRetry * (1 << (delivery.Attempts - 1));
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<(bool Sent, string Result)> SendAsync(IServiceProvider services, DeliveryEntity delivery, CancellationToken ct)
    {
        // The URL and the policy are read at send time: a URL that was removed or that the
        // policy now refuses gets nothing.
        var settings = await services.GetRequiredService<SettingsResolver>().GetWorkspaceAsync(delivery.WorkspaceId, ct);
        if (settings.WebhookUrl is not SettingsUrl.Usable destination)
            return (false, "not_configured");

        string? secret = null;
        try
        {
            switch (await services.GetRequiredService<SecretReader>().ReadAsync(delivery.WorkspaceId, SecretNames.WebhookSecret, destination.Url, ct))
            {
                case SecretRead.Found found:
                    secret = found.Value;
                    break;
                case SecretRead.SavedForAnotherHost:
                    return (false, "secret_saved_for_another_host");
            }
        }
        catch (SecretUnavailableException)
        {
            return (false, "secret_unavailable");
        }

        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        var timestamp = services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = secret is null ? null : Sign(secret, timestamp, body);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        var call = new OutboundCall("webhook", delivery.Event, destination.Url, settings.Policy.AllowPrivateNetworkUrls, settings.RequireHttps, Timeout)
        {
            AbsoluteUri = destination.Url,
            MaxResponseBytes = 64 * 1024,
        };
        try
        {
            using var response = await OutboundSend.RunAsync(() => services.GetRequiredService<ICommentWebhookApi>()
                .Send(content, delivery.Event, delivery.Id.ToString(), timestamp, signature, call, ct));
            return (response.IsSuccessStatusCode, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }
        catch (OutboundException ex)
        {
            return (false, ex.Failure switch
            {
                OutboundFailure.UrlRefused => "url_refused",
                OutboundFailure.InvalidRequest => "invalid_request",
                OutboundFailure.Timeout => "timeout",
                _ => "network",
            });
        }
    }

    /// <summary><c>sha256=</c> and the hex HMAC-SHA-256 of <c>{timestamp}.{body}</c>.</summary>
    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed.AsSpan(timestamp.Length + 1));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
    }
}

/// <summary>The loop that sends webhook events: at once when one is written, and every <see cref="PollInterval"/> for retries.</summary>
public sealed class WebhookWorkerService(WebhookSender sender, WebhookSignal signal, ILogger<WebhookWorkerService> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await sender.RunOnceAsync(stoppingToken))
                    continue;
                await signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The outbox could not be read. The loop must survive a database outage.
                logger.LogError("The webhook worker could not take work ({ExceptionType}). It tries again.", ex.GetType().FullName);
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
