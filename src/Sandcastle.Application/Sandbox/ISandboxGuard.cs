using Sandcastle.Domain.Sandbox;

namespace Sandcastle.Application.Sandbox;

/// <summary>
/// SPEC-154: garante que a conversa tenha uma sandbox RUNNING antes de
/// um run/mensagem — resume Paused/Stopped e <b>reidrata</b> quando o
/// container morreu (Missing/Error): provisiona sandbox nova na mesma
/// spec; o workspace bind-mounted sobrevive (host-side), inclusive o
/// git-dir de checkpoints, então arquivos e checkpoints voltam inteiros.
/// </summary>
public interface IConversationSandboxGuard
{
    /// <summary>
    /// Resolve a sandbox pronta para uso. <see cref="SandboxGuardResult"/>
    /// carrega <c>Rehydrated</c> quando uma nova sandbox foi provisionada
    /// e <c>WorkspaceRestored</c> quando o dir host-side ainda tinha
    /// conteúdo (snapshot completo) — <c>false</c> = workdir vazio.
    /// </summary>
    Task<SandboxGuardResult> EnsureRunningAsync(
        Guid conversationId, CancellationToken ct);
}

/// <summary>Resultado do guard de sandbox da conversa (SPEC-154).</summary>
public sealed record SandboxGuardResult(
    Domain.Sandbox.Sandbox? Sandbox, bool Rehydrated, bool WorkspaceRestored);