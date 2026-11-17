using System.IO;
using System.Net.Http;

using Sandcastle.Application.Identity;

namespace Sandcastle.Infrastructure.Automations;

/// <summary>
/// SPEC-161 RF-003: classifica falhas do disparo em transitórias
/// (retryável) e permanentes (nunca retry — quota/validação não podem
/// queimar LLM duas vezes). Puro e testável.
/// </summary>
public static class AutomationFailureClassifier
{
    /// <summary>
    /// Transitório = falha de infraestrutura (rede, timeout, 5xx do
    /// gateway). <see cref="PlanLimitExceededException"/> (quota) e erros
    /// de validação são permanentes por default — só entram na lista
    /// branca os tipos explicitamente transitórios.
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        // Quota negada é permanente: retry só gastaria de novo.
        if (ex is PlanLimitExceededException)
        {
            return false;
        }

        return ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => true, // timeout do gateway (não shutdown do host)
            TimeoutException => true,
            IOException => true,
            _ => ex.InnerException is not null && IsTransient(ex.InnerException),
        };
    }

    /// <summary>
    /// O <paramref name="ct"/> do host foi cancelado? Nesse caso até um
    /// <see cref="TaskCanceledException"/> é shutdown, não timeout — não
    /// retry nem marca failed.
    /// </summary>
    public static bool IsHostCancellation(
        Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;
}