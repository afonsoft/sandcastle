using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Web;

namespace Sandcastle.Infrastructure.Notifications;

/// <inheritdoc cref="IUserNotificationService" />
public sealed class UserNotificationService(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    ILogger<UserNotificationService> logger) : IUserNotificationService
{
    private static readonly JsonSerializerOptions JsonOpts = new(
        JsonSerializerDefaults.Web);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task NotifyRunStateAsync(
        Guid userId, Guid conversationId, string eventKind,
        string? title, CancellationToken ct)
    {
        try
        {
            var prefsJson = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.PreferencesJson)
                .FirstOrDefaultAsync(ct);
            var prefs = ReadPrefs(prefsJson);
            if (prefs.WebhookUrl is null
                || !(eventKind == "run_finished" ? prefs.RunFinished
                    : prefs.RunFailed))
            {
                return;
            }

            var payload = new
            {
                @event = eventKind,
                conversation_id = conversationId,
                title,
                ts = DateTimeOffset.UtcNow,
            };
            await PostAsync(prefs.WebhookUrl, payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "user notification webhook falhou (user {UserId})", userId);
        }
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? Error)> ValidateUrlAsync(
        string webhookUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https")
        {
            return (false, "webhook_url must be an https URL");
        }

        return await SsrfGuard.IsBlockedAsync(uri, ct)
            ? (false, "webhook_url blocked by SSRF guard")
            : (true, null);
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string? Error)> SendTestAsync(
        string webhookUrl, CancellationToken ct)
    {
        var (valid, error) = await ValidateUrlAsync(webhookUrl, ct);
        if (!valid)
        {
            return (false, error);
        }

        try
        {
            var ok = await PostAsync(webhookUrl, new
            {
                @event = "test",
                ts = DateTimeOffset.UtcNow,
            }, ct);
            return ok ? (true, null) : (false, "webhook did not return 2xx");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task NotifyAutomationDisabledAsync(
        Guid userId, Guid automationId, string automationName,
        int consecutiveFailures, CancellationToken ct)
    {
        try
        {
            var prefsJson = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.PreferencesJson)
                .FirstOrDefaultAsync(ct);
            var prefs = ReadPrefs(prefsJson);
            if (prefs.WebhookUrl is null)
            {
                return;
            }

            await PostAsync(prefs.WebhookUrl, new
            {
                @event = "automation_disabled",
                automation_id = automationId,
                name = automationName,
                consecutive_failures = consecutiveFailures,
                ts = DateTimeOffset.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "automation_disabled notification falhou (user {UserId})",
                userId);
        }
    }

    private async Task<bool> PostAsync(
        string url, object payload, CancellationToken ct)
    {
        // 1 tentativa + 1 retry (SPEC-158 RF-004).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var http = httpFactory.CreateClient("user-notify");
                using var cts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);
                using var res = await http.PostAsJsonAsync(
                    url, payload, JsonOpts, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    return true;
                }

                logger.LogWarning(
                    "user webhook {Url} -> {Status} (attempt {N})",
                    url, (int)res.StatusCode, attempt + 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "user webhook {Url} attempt {N} failed", url, attempt + 1);
            }
        }

        return false;
    }

    /// <summary>Lê o objeto <c>notifications</c> das preferências.</summary>
    public static UserNotificationPrefs ReadPrefs(string? preferencesJson)
    {
        if (string.IsNullOrWhiteSpace(preferencesJson))
        {
            return UserNotificationPrefs.Default;
        }

        try
        {
            using var doc = JsonDocument.Parse(preferencesJson);
            if (!doc.RootElement.TryGetProperty("notifications",
                    out var n)
                || n.ValueKind != JsonValueKind.Object)
            {
                return UserNotificationPrefs.Default;
            }

            return new UserNotificationPrefs(
                RunFinished: n.TryGetProperty("run_finished", out var f)
                    && f.GetBoolean(),
                RunFailed: n.TryGetProperty("run_failed", out var e)
                    && e.GetBoolean(),
                WebhookUrl: n.TryGetProperty("webhook_url", out var w)
                    && w.ValueKind == JsonValueKind.String
                        ? w.GetString() : null);
        }
        catch (JsonException)
        {
            return UserNotificationPrefs.Default;
        }
    }
}