namespace Sandcastle.Infrastructure.Automations;

/// <summary>
/// SPEC-161: configuração do scheduler de automations — seção
/// <c>Automation</c>. Limites conservadores por default: uma automation
/// quebrada não pode bloquear as demais nem queimar quota em retry.
/// </summary>
public sealed class AutomationOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string SectionName = "Automation";

    /// <summary>Dispatch paralelo máximo por tick (RF-001).</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>
    /// Graça de misfire em segundos (RF-002): <c>NextRunAt</c> mais velho
    /// que isso é tratado como missed (política da automation decide).
    /// </summary>
    public int MisfireGraceSec { get; set; } = 300;

    /// <summary>Tentativas para falhas transitórias (RF-003).</summary>
    public int MaxTransientAttempts { get; set; } = 3;

    /// <summary>Backoff base do retry transitório (ms, exponencial).</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>Backoff máximo do retry transitório (ms).</summary>
    public int RetryMaxDelayMs { get; set; } = 8000;

    /// <summary>
    /// Falhas consecutivas que abrem o circuit breaker (RF-004): a
    /// automation é desabilitada e o dono notificado.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 5;
}