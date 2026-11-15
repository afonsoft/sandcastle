using System.Text.Json;

using Microsoft.Extensions.Logging;

using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Sandboxing;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Infrastructure.Sandbox;

/// <summary>
/// SPEC-154: implementação do <see cref="IConversationSandboxGuard"/>.
/// Reidratação não precisa de export/tar: o workspace é bind-mount
/// host-side (<c>{WorkspaceRoot}/{conversationId}</c> → <c>/workspace</c>),
/// então container novo na mesma conversa já enxerga arquivos e o
/// git-dir de checkpoints (<c>/workspace/.sc-checkpoints</c>) — revert
/// pós-reidratação funciona sem restore dedicado.
/// </summary>
public sealed class ConversationSandboxGuard(
    AppDbContext db,
    ISandboxService sandboxes,
    IEventService events,
    WorkspaceProvisioner provisioner,
    ILogger<ConversationSandboxGuard> logger) : IConversationSandboxGuard
{
    /// <inheritdoc />
    public async Task<SandboxGuardResult> EnsureRunningAsync(
        Guid conversationId, CancellationToken ct)
    {
        var record = await db.Conversations.FindAsync([conversationId], ct);
        if (record?.SandboxId is null)
        {
            return new SandboxGuardResult(null, false, false);
        }

        var sandbox = await sandboxes.GetAsync(record.SandboxId.Value, ct);
        switch (sandbox?.Status)
        {
            case Domain.Sandbox.SandboxStatus.Running:
                return new SandboxGuardResult(sandbox, false, true);
            case Domain.Sandbox.SandboxStatus.Paused
                or Domain.Sandbox.SandboxStatus.Stopped:
                sandbox = await sandboxes.ResumeAsync(
                    record.SandboxId.Value, ct);
                return new SandboxGuardResult(
                    sandbox, false, true);
            case Domain.Sandbox.SandboxStatus.Missing
                or Domain.Sandbox.SandboxStatus.Error:
                return await RehydrateAsync(record, sandbox, ct);
            default:
                return new SandboxGuardResult(sandbox, false, true);
        }
    }

    /// <summary>
    /// Provisiona sandbox nova na mesma spec e reponta a conversa; o
    /// evento <c>sandbox_rehydrated</c> registra se o workdir veio com
    /// conteúdo (<c>workspace_restored=false</c> = workdir vazio —
    /// snapshot inexistente p/ providers sem bind-mount).
    /// </summary>
    private async Task<SandboxGuardResult> RehydrateAsync(
        ConversationRecord record,
        DomainSandbox? dead, CancellationToken ct)
    {
        var conversationId = record.Id;
        var specId = dead?.SpecId ?? Guid.Empty;
        var oldSandboxId = record.SandboxId!.Value;

        var fresh = await sandboxes.StartAsync(specId, conversationId, ct);
        record.SandboxId = fresh.Id;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var restored = WorkspaceHasContent(conversationId);
        await events.AppendAsync(conversationId,
            new ConversationStateUpdateEvent
            {
                Key = "sandbox_rehydrated",
                Value = JsonSerializer.SerializeToElement(new
                {
                    rehydrated = true,
                    workspace_restored = restored,
                    sandbox_id = fresh.Id.ToString(),
                    previous_sandbox_id = oldSandboxId.ToString(),
                }, EventJson.Options),
            }, ct);
        logger.LogInformation(
            "Sandbox rehydrated for conversation {ConversationId}: " +
            "{Old} → {New} (workspace_restored={Restored})",
            conversationId, oldSandboxId, fresh.Id, restored);

        // Remove o registro morto — best-effort; o container já não existe.
        try
        {
            await sandboxes.DeleteAsync(oldSandboxId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "cleanup of dead sandbox {SandboxId} failed", oldSandboxId);
        }

        return new SandboxGuardResult(fresh, true, restored);
    }

    private bool WorkspaceHasContent(Guid conversationId)
    {
        try
        {
            var path = provisioner.PreparePath(conversationId);
            return Directory.Exists(path)
                && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            logger.LogDebug(ex,
                "workspace probe failed for {ConversationId}", conversationId);
            return false;
        }
    }
}