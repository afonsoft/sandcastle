using System.Text.Json;

namespace Sandcastle.Application.Workflows;

/// <summary>SPEC-183: factory grouping automations and workflows.</summary>
public sealed record FactoryInfo(
    Guid Id, Guid? OrgId, string OwnerUserId, string Name,
    string? Description, int AutomationCount, int WorkflowCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>SPEC-183: factory create/update payload.</summary>
public sealed record FactoryUpsert(string? Name, string? Description);

/// <summary>SPEC-183: named/versioned workflow definition.</summary>
public sealed record WorkflowInfo(
    Guid Id, Guid? OrgId, string OwnerUserId, Guid? FactoryId,
    string Name, string? Description, int Version, string DefinitionJson,
    bool AutonomousDelivery, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>SPEC-183: workflow create/update payload.</summary>
public sealed record WorkflowUpsert(
    string? Name, string? Description, Guid? FactoryId,
    string? DefinitionJson, bool? AutonomousDelivery);

/// <summary>SPEC-183: auditable workflow run state.</summary>
public sealed record WorkflowRunInfo(
    Guid Id, Guid WorkflowId, string Status, int CurrentStepIndex,
    string? CurrentStepName, Guid? ConversationId, string? Error,
    JsonElement Inputs, JsonElement Audit, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Input map used when starting a workflow run.</summary>
public sealed record WorkflowRunStart(Dictionary<string, JsonElement>? Inputs);

/// <summary>Human decision for an approval step.</summary>
public sealed record WorkflowApprovalRequest(bool Approved, string? Comment);

/// <summary>Recommended automation template shown in the event center.</summary>
public sealed record AutomationTemplateInfo(
    string Id, string Name, string Description, string Kind,
    string Prompt, string ScheduleKind, int? IntervalMinutes,
    string? CronExpression, string? ApprovalPreset);

/// <summary>Unified automation/workflow event-center row.</summary>
public sealed record AutomationEventInfo(
    string Id, string Source, Guid SourceId, Guid RunId,
    Guid? FactoryId, string? FactoryName, string Name, string Status,
    string? Repository, Guid? ConversationId, DateTimeOffset OccurredAt,
    string DeepLink, string? Error);

/// <summary>Factory CRUD + grouping service.</summary>
public interface IFactoryService
{
    /// <summary>Lists owner-visible factories.</summary>
    Task<IReadOnlyList<FactoryInfo>> ListAsync(Guid ownerUserId, CancellationToken ct);
    /// <summary>Gets one factory (owner scoped).</summary>
    Task<FactoryInfo?> GetAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    /// <summary>Creates a factory.</summary>
    Task<(FactoryInfo? Info, string? Error)> CreateAsync(
        FactoryUpsert request, Guid ownerUserId, Guid? orgId, CancellationToken ct);
    /// <summary>Renames/updates a factory.</summary>
    Task<(FactoryInfo? Info, string? Error)> UpdateAsync(
        Guid id, FactoryUpsert request, Guid ownerUserId, CancellationToken ct);
    /// <summary>Deletes a factory; members remain but lose the grouping.</summary>
    Task<bool> DeleteAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    /// <summary>Lists automations assigned to a factory.</summary>
    Task<IReadOnlyList<Automations.AutomationInfo>> ListAutomationsAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);
}

/// <summary>Workflow CRUD + auditable run orchestration.</summary>
public interface IWorkflowService
{
    /// <summary>Lists workflows, optionally filtered by factory.</summary>
    Task<IReadOnlyList<WorkflowInfo>> ListAsync(
        Guid ownerUserId, Guid? factoryId, CancellationToken ct);
    /// <summary>Gets one workflow (owner scoped).</summary>
    Task<WorkflowInfo?> GetAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    /// <summary>Creates a validated workflow.</summary>
    Task<(WorkflowInfo? Info, string? Error)> CreateAsync(
        WorkflowUpsert request, Guid ownerUserId, Guid? orgId, CancellationToken ct);
    /// <summary>Updates a workflow and increments its immutable version counter.</summary>
    Task<(WorkflowInfo? Info, string? Error)> UpdateAsync(
        Guid id, WorkflowUpsert request, Guid ownerUserId, CancellationToken ct);
    /// <summary>Deletes a workflow and its runs.</summary>
    Task<bool> DeleteAsync(Guid id, Guid ownerUserId, CancellationToken ct);
    /// <summary>Starts a validated run; returns 409-shaped error when one is active.</summary>
    Task<(WorkflowRunInfo? Run, string? Error)> StartRunAsync(
        Guid workflowId, WorkflowRunStart request, Guid ownerUserId, CancellationToken ct);
    /// <summary>Lists runs of a workflow.</summary>
    Task<IReadOnlyList<WorkflowRunInfo>> ListRunsAsync(
        Guid workflowId, Guid ownerUserId, int take, CancellationToken ct);
    /// <summary>Gets and refreshes one run.</summary>
    Task<WorkflowRunInfo?> GetRunAsync(Guid runId, Guid ownerUserId, CancellationToken ct);
    /// <summary>Applies the hard human approval gate.</summary>
    Task<(WorkflowRunInfo? Run, string? Error)> ApproveRunAsync(
        Guid runId, WorkflowApprovalRequest request, Guid ownerUserId, CancellationToken ct);
    /// <summary>Refreshes active workflow runs (worker path).</summary>
    Task<int> RefreshActiveRunsAsync(CancellationToken ct);
    /// <summary>Recommended automation templates.</summary>
    IReadOnlyList<AutomationTemplateInfo> ListTemplates();
    /// <summary>Unified automation/workflow event center.</summary>
    Task<IReadOnlyList<AutomationEventInfo>> ListEventsAsync(
        Guid ownerUserId, Guid? factoryId, string? status, string? repository,
        int take, CancellationToken ct);
}