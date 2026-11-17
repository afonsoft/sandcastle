namespace Sandcastle.Application.Identity;

/// <summary>
/// SPEC-158 RF-004: preferências de notificação do usuário — persistidas em
/// <c>UserRecord.PreferencesJson["notifications"]</c> (objeto JSON).
/// Webhook HTTPS-only + SSRF-guard; dispatch fire-and-forget no término
/// de run (finished/failed).
/// </summary>
public sealed record UserNotificationPrefs(
    bool RunFinished,
    bool RunFailed,
    string? WebhookUrl)
{
    /// <summary>Padrão: tudo desligado.</summary>
    public static readonly UserNotificationPrefs Default = new(
        RunFinished: false, RunFailed: false, WebhookUrl: null);
}

/// <summary>Dispatch de notificações do usuário (webhook).</summary>
public interface IUserNotificationService
{
    /// <summary>
    /// Dispara o webhook do usuário quando a run termina/falha, se
    /// habilitado nas preferências. Nunca lança — fire-and-forget.
    /// </summary>
    Task NotifyRunStateAsync(
        Guid userId, Guid conversationId, string eventKind,
        string? title, CancellationToken ct);

    /// <summary>Valida HTTPS + SSRF guard sem disparar request.</summary>
    Task<(bool Ok, string? Error)> ValidateUrlAsync(
        string webhookUrl, CancellationToken ct);

    /// <summary>
    /// Valida uma URL de webhook (HTTPS + SSRF guard) e dispara um
    /// evento <c>test</c>. Retorna (ok, erro?).
    /// </summary>
    Task<(bool Ok, string? Error)> SendTestAsync(
        string webhookUrl, CancellationToken ct);

    /// <summary>
    /// SPEC-161 RF-004: avisa o dono que uma automation foi desabilitada
    /// pelo circuit breaker (evento <c>automation_disabled</c>). Nunca
    /// lança — fire-and-forget; sem webhook configurado, no-op.
    /// </summary>
    Task NotifyAutomationDisabledAsync(
        Guid userId, Guid automationId, string automationName,
        int consecutiveFailures, CancellationToken ct);
}