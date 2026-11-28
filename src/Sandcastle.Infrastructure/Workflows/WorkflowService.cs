using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Workflows;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Workflows;

/// <summary>
/// SPEC-183 RF-001/RF-004: named workflow orchestration. The engine is
/// deliberately agent-first: agent/script steps create audited conversations;
/// approval steps are hard human gates. Runs advance lazily and through the
/// hosted worker by reconciling conversation execution state.
/// </summary>
public sealed class WorkflowService(
    AppDbContext db,
    IAppConversationService conversations,
    IAuditService? audit = null) : IWorkflowService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] ActiveStatuses =
        ["running", "waiting_agent", "waiting_approval"];

    private sealed record InputDef(string Name, string Type, bool Required, string? Default);
    private sealed record StepDef(string Name, string Type, string? Prompt, string? Script);
    private sealed record RetryDef(int MaxAttempts, int MaxSteps);
    private sealed record Definition(
        List<InputDef> Inputs, List<StepDef> Steps, RetryDef Retry);

    private static Definition DefaultAutonomousDefinition() => new(
        [
            new InputDef("repository", "string", true, null),
            new InputDef("task", "string", true, null),
        ],
        [
            new StepDef("plan", "agent",
                "Create a concise implementation plan for {{task}} in {{repository}}. Do not implement yet.", null),
            new StepDef("approve_plan", "approval", "Human approval is required before implementation.", null),
            new StepDef("implement_validate", "agent",
                "Implement {{task}} in {{repository}}, run validation, fix failures, and report evidence.", null),
            new StepDef("pr_ci_review", "agent",
                "Open or update the pull request, watch CI, address review/fix failures, and stop at merge-ready. Do not merge without explicit human approval.", null),
            new StepDef("approve_merge_ready", "approval",
                "Human approval is required before final merge-ready handoff.", null),
            new StepDef("merge_ready", "agent",
                "Summarize the delivery evidence and merge-ready state. Do not bypass branch protection or human gates.", null),
        ],
        new RetryDef(2, 8));

    private static Definition ParseDefinition(WorkflowRecord workflow)
    {
        try
        {
            using var doc = JsonDocument.Parse(workflow.DefinitionJson);
            return ParseDefinition(doc.RootElement, workflow.AutonomousDelivery)
                ?? DefaultAutonomousDefinition();
        }
        catch (JsonException)
        {
            return DefaultAutonomousDefinition();
        }
    }

    private static Definition? ParseDefinition(JsonElement root, bool autonomous)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var inputs = new List<InputDef>();
        if (root.TryGetProperty("inputs", out var inputsEl)
            && inputsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inputsEl.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : "string";
                if (string.IsNullOrWhiteSpace(name)) return null;
                inputs.Add(new InputDef(name.Trim(), type ?? "string",
                    item.TryGetProperty("required", out var r) && r.GetBoolean(),
                    item.TryGetProperty("default", out var d) ? d.GetString() : null));
            }
        }

        var steps = new List<StepDef>();
        if (root.TryGetProperty("steps", out var stepsEl)
            && stepsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stepsEl.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || type is not ("agent" or "script" or "approval"))
                    return null;
                steps.Add(new StepDef(name.Trim(), type,
                    item.TryGetProperty("prompt", out var p) ? p.GetString() : null,
                    item.TryGetProperty("script", out var sc) ? sc.GetString() : null));
            }
        }

        var retry = new RetryDef(1, 20);
        if (root.TryGetProperty("retry", out var retryEl)
            && retryEl.ValueKind == JsonValueKind.Object)
        {
            retry = new RetryDef(
                retryEl.TryGetProperty("max_attempts", out var ma)
                    ? Math.Clamp(ma.GetInt32(), 1, 5) : 1,
                retryEl.TryGetProperty("max_steps", out var ms)
                    ? Math.Clamp(ms.GetInt32(), 1, 50) : 20);
        }

        if (steps.Count == 0 && autonomous) return DefaultAutonomousDefinition();
        return steps.Count == 0 ? null : new Definition(inputs, steps, retry);
    }

    internal static string? ValidateDefinition(string? definitionJson, bool autonomous)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return autonomous ? null : "definition_json required";
        }
        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            var parsed = ParseDefinition(doc.RootElement, autonomous);
            return parsed is null ? "invalid workflow definition" : null;
        }
        catch (JsonException)
        {
            return "definition_json must be valid JSON";
        }
    }

    internal static string DefaultDefinitionJson() => JsonSerializer.Serialize(new
    {
        inputs = new[]
        {
            new { name = "repository", type = "string", required = true },
            new { name = "task", type = "string", required = true },
        },
        retry = new { max_attempts = 2, max_steps = 8 },
        steps = Array.Empty<object>(),
    }, Json);

    private static WorkflowInfo ToInfo(WorkflowRecord r) => new(
        r.Id, r.OrgId, r.OwnerUserId, r.FactoryId, r.Name, r.Description,
        r.Version, r.DefinitionJson, r.AutonomousDelivery, r.CreatedAt, r.UpdatedAt);

    private static WorkflowRunInfo ToRunInfo(WorkflowRunRecord r)
    {
        using var inputs = JsonDocument.Parse(r.InputsJson);
        using var audit = JsonDocument.Parse(r.AuditJson);
        return new WorkflowRunInfo(r.Id, r.WorkflowId, r.Status,
            r.CurrentStepIndex, r.CurrentStepName, r.ConversationId, r.Error,
            inputs.RootElement.Clone(), audit.RootElement.Clone(), r.CreatedAt,
            r.StartedAt, r.FinishedAt);
    }

    private static List<JsonElement> AuditEntries(WorkflowRunRecord run) =>
        JsonSerializer.Deserialize<List<JsonElement>>(run.AuditJson, Json) ?? [];

    private static void AddAudit(WorkflowRunRecord run, string type, object data)
    {
        var entries = AuditEntries(run);
        entries.Add(JsonSerializer.SerializeToElement(new
        {
            at = DateTimeOffset.UtcNow,
            type,
            data,
        }, Json));
        run.AuditJson = JsonSerializer.Serialize(entries, Json);
    }

    private async Task<(WorkflowRecord? Workflow, string? Error)> ValidateFactoryAsync(
        Guid? factoryId, Guid ownerUserId, Guid? orgId, CancellationToken ct)
    {
        if (!factoryId.HasValue) return (null, null);
        var owner = ownerUserId.ToString();
        var factory = await db.Factories.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == factoryId.Value
                && f.OwnerUserId == owner, ct);
        return factory is null ? (null, "factory_not_found") : (null, null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowInfo>> ListAsync(
        Guid ownerUserId, Guid? factoryId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var query = db.Workflows.AsNoTracking().Where(w => w.OwnerUserId == owner);
        if (factoryId.HasValue) query = query.Where(w => w.FactoryId == factoryId.Value);
        return (await query.OrderBy(w => w.Name).ThenBy(w => w.Id).ToListAsync(ct))
            .Select(ToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<WorkflowInfo?> GetAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id && w.OwnerUserId == owner, ct);
        return row is null ? null : ToInfo(row);
    }

    /// <inheritdoc />
    public async Task<(WorkflowInfo? Info, string? Error)> CreateAsync(
        WorkflowUpsert request, Guid ownerUserId, Guid? orgId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return (null, "name required");
        var autonomous = request.AutonomousDelivery == true;
        var definition = string.IsNullOrWhiteSpace(request.DefinitionJson)
            ? DefaultDefinitionJson()
            : request.DefinitionJson!;
        if (ValidateDefinition(definition, autonomous) is { } error) return (null, error);
        if (await ValidateFactoryAsync(request.FactoryId, ownerUserId, orgId, ct) is { Error: { } fe })
            return (null, fe);
        var now = DateTimeOffset.UtcNow;
        var row = new WorkflowRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerUserId = ownerUserId.ToString(),
            FactoryId = request.FactoryId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Version = 1,
            DefinitionJson = definition,
            AutonomousDelivery = autonomous,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Workflows.Add(row);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("workflow_create", ownerUserId.ToString(), orgId,
                "workflow", row.Id.ToString(), metadata: new { row.Name, row.Version },
                cancellationToken: CancellationToken.None);
        }
        return (ToInfo(row), null);
    }

    /// <inheritdoc />
    public async Task<(WorkflowInfo? Info, string? Error)> UpdateAsync(
        Guid id, WorkflowUpsert request, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Workflows.FirstOrDefaultAsync(
            w => w.Id == id && w.OwnerUserId == owner, ct);
        if (row is null) return (null, "not found");
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return (null, "name required");
            row.Name = request.Name.Trim();
        }
        if (request.Description is not null) row.Description = request.Description.Trim();
        if (request.FactoryId.HasValue)
        {
            if (await ValidateFactoryAsync(request.FactoryId, ownerUserId, row.OrgId, ct) is { Error: { } fe })
                return (null, fe);
            row.FactoryId = request.FactoryId;
        }
        if (request.AutonomousDelivery.HasValue) row.AutonomousDelivery = request.AutonomousDelivery.Value;
        if (request.DefinitionJson is not null)
        {
            if (ValidateDefinition(request.DefinitionJson, row.AutonomousDelivery) is { } error)
                return (null, error);
            row.DefinitionJson = request.DefinitionJson;
        }
        row.Version++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("workflow_update", owner, row.OrgId,
                "workflow", row.Id.ToString(), metadata: new { row.Name, row.Version },
                cancellationToken: CancellationToken.None);
        }
        return (ToInfo(row), null);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Workflows.FirstOrDefaultAsync(
            w => w.Id == id && w.OwnerUserId == owner, ct);
        if (row is null) return false;
        db.Workflows.Remove(row);
        await db.WorkflowRuns.Where(r => r.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string? ValidateInputs(Definition definition, Dictionary<string, JsonElement> inputs)
    {
        foreach (var input in definition.Inputs)
        {
            if (!inputs.TryGetValue(input.Name, out var value))
            {
                if (input.Required && input.Default is null) return $"input '{input.Name}' required";
                continue;
            }
            var ok = input.Type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false,
            };
            if (!ok) return $"input '{input.Name}' must be {input.Type}";
        }
        return null;
    }

    private static Dictionary<string, JsonElement> MaterializeInputs(
        Definition definition, Dictionary<string, JsonElement>? inputs)
    {
        var result = inputs is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(inputs, StringComparer.Ordinal);
        foreach (var input in definition.Inputs.Where(i => i.Default is not null))
        {
            if (!result.ContainsKey(input.Name))
                result[input.Name] = JsonSerializer.SerializeToElement(input.Default);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<(WorkflowRunInfo? Run, string? Error)> StartRunAsync(
        Guid workflowId, WorkflowRunStart request, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var workflow = await db.Workflows.FirstOrDefaultAsync(
            w => w.Id == workflowId && w.OwnerUserId == owner, ct);
        if (workflow is null) return (null, "not found");
        var active = await db.WorkflowRuns.AnyAsync(r => r.WorkflowId == workflowId
            && ActiveStatuses.Contains(r.Status), ct);
        if (active) return (null, "workflow run already in progress");
        var definition = ParseDefinition(workflow);
        var inputs = MaterializeInputs(definition, request.Inputs);
        if (ValidateInputs(definition, inputs) is { } inputError) return (null, inputError);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRunRecord
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            OwnerUserId = owner,
            Status = "running",
            CurrentStepIndex = 0,
            InputsJson = JsonSerializer.Serialize(inputs, Json),
            AuditJson = "[]",
            AttemptsJson = "{}",
            CreatedAt = now,
            StartedAt = now,
            CreatedAtTicks = now.UtcTicks,
        };
        AddAudit(run, "run_started", new { workflow.Version, workflow.AutonomousDelivery });
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await AdvanceAsync(run, workflow, definition, ct);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("workflow_run_start", owner, workflow.OrgId,
                "workflow_run", run.Id.ToString(), metadata: new { workflow.Name },
                cancellationToken: CancellationToken.None);
        }
        return (ToRunInfo(run), null);
    }

    private static string Render(string? template, string inputsJson)
    {
        var text = template ?? "";
        using var doc = JsonDocument.Parse(inputsJson);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : prop.Value.GetRawText();
            text = text.Replace("{{" + prop.Name + "}}", value, StringComparison.Ordinal);
        }
        return text;
    }

    private async Task AdvanceAsync(
        WorkflowRunRecord run, WorkflowRecord workflow, Definition definition, CancellationToken ct)
    {
        var budget = Math.Max(1, definition.Retry.MaxSteps);
        var advanced = 0;
        while (advanced++ < budget)
        {
            if (run.CurrentStepIndex >= definition.Steps.Count)
            {
                run.Status = "succeeded";
                run.FinishedAt = DateTimeOffset.UtcNow;
                run.CurrentStepName = null;
                run.ConversationId = null;
                AddAudit(run, "run_succeeded", new { });
                return;
            }

            var step = definition.Steps[run.CurrentStepIndex];
            run.CurrentStepName = step.Name;
            if (step.Type == "approval")
            {
                run.Status = "waiting_approval";
                run.ConversationId = null;
                AddAudit(run, "approval_waiting", new { step.Name, step.Prompt });
                return;
            }

            if (run.ConversationId is null)
            {
                await StartStepConversationAsync(run, workflow, step, ct);
                return;
            }

            var conversation = await conversations.GetAsync(run.ConversationId.Value, ct);
            if (conversation is null)
            {
                await FailAsync(run, "conversation not found", ct);
                return;
            }
            if (conversation.ExecutionStatus == ConversationExecutionStatus.Finished)
            {
                AddAudit(run, "step_succeeded", new { step.Name, run.ConversationId });
                run.ConversationId = null;
                run.CurrentStepIndex++;
                run.Status = "running";
                continue;
            }
            if (conversation.ExecutionStatus is ConversationExecutionStatus.Error
                or ConversationExecutionStatus.Archived)
            {
                var attempts = JsonSerializer.Deserialize<Dictionary<string, int>>(run.AttemptsJson, Json) ?? [];
                var key = run.CurrentStepIndex.ToString();
                attempts[key] = attempts.GetValueOrDefault(key) + 1;
                run.AttemptsJson = JsonSerializer.Serialize(attempts, Json);
                if (attempts[key] < definition.Retry.MaxAttempts)
                {
                    AddAudit(run, "step_retry", new { step.Name, attempt = attempts[key] });
                    run.ConversationId = null;
                    continue;
                }
                AddAudit(run, "step_failed", new { step.Name, run.ConversationId });
                await FailAsync(run, $"step '{step.Name}' failed", ct);
                return;
            }

            run.Status = "waiting_agent";
            AddAudit(run, "agent_waiting", new { step.Name, run.ConversationId });
            return;
        }
        await FailAsync(run, "workflow step budget exhausted", ct);
    }

    private async Task StartStepConversationAsync(
        WorkflowRunRecord run, WorkflowRecord workflow, StepDef step, CancellationToken ct)
    {
        var prompt = Render(step.Prompt, run.InputsJson);
        if (step.Type == "script")
        {
            var script = Render(step.Script, run.InputsJson);
            prompt = "Execute this script in the workspace, capture exit code and output, fix only script-environment issues if safe, then finish with concise evidence:\n\n"
                + script + "\n\nContext: " + prompt;
        }
        else
        {
            prompt = prompt + "\n\nWorkflow hard gates: do not merge or bypass review/branch protection; stop at merge-ready and report evidence.";
        }

        var ownerId = Guid.Parse(run.OwnerUserId);
        var task = await conversations.CreateForUserAsync(ownerId, workflow.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [prompt], Run: true),
                InputString(run.InputsJson, "repository"), null, null, null,
                Mode: "build"), ct);
        run.ConversationId = task.AppConversationId;
        run.Status = "waiting_agent";
        AddAudit(run, "step_started", new { step.Name, step.Type, run.ConversationId });
    }

    private static string? InputString(string inputsJson, string name)
    {
        using var doc = JsonDocument.Parse(inputsJson);
        return doc.RootElement.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    }

    private async Task FailAsync(WorkflowRunRecord run, string error, CancellationToken ct)
    {
        run.Status = "failed";
        run.Error = error;
        run.FinishedAt = DateTimeOffset.UtcNow;
        AddAudit(run, "run_failed", new { error });
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowRunInfo>> ListRunsAsync(
        Guid workflowId, Guid ownerUserId, int take, CancellationToken ct)
    {
        if (await GetAsync(workflowId, ownerUserId, ct) is null) return [];
        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId)
            .OrderByDescending(r => r.CreatedAtTicks)
            .Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        return rows.Select(ToRunInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<WorkflowRunInfo?> GetRunAsync(
        Guid runId, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(
            r => r.Id == runId && r.OwnerUserId == owner, ct);
        if (run is null) return null;
        await RefreshRunAsync(run, ct);
        await db.SaveChangesAsync(ct);
        return ToRunInfo(run);
    }

    private async Task RefreshRunAsync(WorkflowRunRecord run, CancellationToken ct)
    {
        if (!ActiveStatuses.Contains(run.Status) || run.Status == "waiting_approval") return;
        var workflow = await db.Workflows.FirstOrDefaultAsync(w => w.Id == run.WorkflowId, ct);
        if (workflow is null)
        {
            await FailAsync(run, "workflow not found", ct);
            return;
        }
        await AdvanceAsync(run, workflow, ParseDefinition(workflow), ct);
    }

    /// <inheritdoc />
    public async Task<(WorkflowRunInfo? Run, string? Error)> ApproveRunAsync(
        Guid runId, WorkflowApprovalRequest request, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(
            r => r.Id == runId && r.OwnerUserId == owner, ct);
        if (run is null) return (null, "not found");
        if (run.Status != "waiting_approval") return (null, "run is not waiting for approval");
        if (!request.Approved)
        {
            run.Status = "rejected";
            run.FinishedAt = DateTimeOffset.UtcNow;
            AddAudit(run, "approval_rejected", new { request.Comment });
            await db.SaveChangesAsync(ct);
            return (ToRunInfo(run), null);
        }
        AddAudit(run, "approval_approved", new { request.Comment });
        run.Status = "running";
        run.CurrentStepIndex++;
        var workflow = await db.Workflows.FirstOrDefaultAsync(w => w.Id == run.WorkflowId, ct);
        if (workflow is null) return (null, "workflow not found");
        await AdvanceAsync(run, workflow, ParseDefinition(workflow), ct);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("workflow_approval", owner, workflow.OrgId,
                "workflow_run", run.Id.ToString(), metadata: new { request.Approved },
                cancellationToken: CancellationToken.None);
        }
        return (ToRunInfo(run), null);
    }

    /// <inheritdoc />
    public async Task<int> RefreshActiveRunsAsync(CancellationToken ct)
    {
        var rows = await db.WorkflowRuns
            .Where(r => ActiveStatuses.Contains(r.Status) && r.Status != "waiting_approval")
            .OrderBy(r => r.CreatedAtTicks).Take(25).ToListAsync(ct);
        foreach (var run in rows) await RefreshRunAsync(run, ct);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<AutomationTemplateInfo> ListTemplates() =>
    [
        new("nightly-review", "Nightly review", "Review recent repository activity every night.",
            "task", "Review recent changes, identify risks, and open concise findings.", "cron", null,
            "0 2 * * *", "plan"),
        new("weekly-dependency-audit", "Weekly dependency audit", "Audit dependency health weekly.",
            "task", "Audit dependency updates and security advisories; propose a safe update plan.", "cron", null,
            "0 8 * * 1", "allow-readonly"),
        new("pr-watch", "PR watch", "Watch pull requests and summarize blockers.",
            "task", "Inspect open pull requests, CI and review state; summarize blockers.", "interval", 30,
            null, "allow-readonly"),
        new("autonomous-delivery", "Autonomous delivery", "Hard-gated plan→implement→validate→PR→CI→merge-ready loop.",
            "task", "Use workflow autonomous delivery with explicit approval gates.", "once", null,
            null, "plan"),
    ];

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationEventInfo>> ListEventsAsync(
        Guid ownerUserId, Guid? factoryId, string? status, string? repository,
        int take, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var automations = await db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == owner).ToListAsync(ct);
        var automationIds = automations.Select(a => a.Id).ToHashSet();
        var factories = await db.Factories.AsNoTracking()
            .Where(f => f.OwnerUserId == owner).ToListAsync(ct);
        var factoryNames = factories.ToDictionary(f => f.Id, f => f.Name);
        var automationRuns = await db.AutomationRuns.AsNoTracking()
            .Where(r => automationIds.Contains(r.AutomationId))
            .OrderByDescending(r => r.StartedAtTicks).Take(200).ToListAsync(ct);
        var rows = new List<AutomationEventInfo>();
        foreach (var run in automationRuns)
        {
            var automation = automations.First(a => a.Id == run.AutomationId);
            rows.Add(new AutomationEventInfo(
                $"automation:{run.Id}", "automation", automation.Id, run.Id,
                automation.FactoryId,
                automation.FactoryId is { } fid && factoryNames.TryGetValue(fid, out var fn) ? fn : null,
                automation.Name, run.Status, automation.SelectedRepository,
                run.ConversationId, run.StartedAt,
                run.ConversationId is { } cid ? $"/conversations/{cid}" : $"/automations/{automation.Id}",
                run.Error));
        }

        var workflows = await db.Workflows.AsNoTracking()
            .Where(w => w.OwnerUserId == owner).ToListAsync(ct);
        var workflowIds = workflows.Select(w => w.Id).ToHashSet();
        var workflowRuns = await db.WorkflowRuns.AsNoTracking()
            .Where(r => workflowIds.Contains(r.WorkflowId))
            .OrderByDescending(r => r.CreatedAtTicks).Take(200).ToListAsync(ct);
        foreach (var run in workflowRuns)
        {
            var workflow = workflows.First(w => w.Id == run.WorkflowId);
            rows.Add(new AutomationEventInfo(
                $"workflow:{run.Id}", "workflow", workflow.Id, run.Id,
                workflow.FactoryId,
                workflow.FactoryId is { } fid && factoryNames.TryGetValue(fid, out var fn) ? fn : null,
                workflow.Name, run.Status, InputString(run.InputsJson, "repository"),
                run.ConversationId, run.CreatedAt,
                run.ConversationId is { } cid ? $"/conversations/{cid}" : $"/workflows/{workflow.Id}",
                run.Error));
        }

        IEnumerable<AutomationEventInfo> filtered = rows;
        if (factoryId.HasValue) filtered = filtered.Where(e => e.FactoryId == factoryId.Value);
        if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(repository))
        {
            filtered = filtered.Where(e => string.Equals(e.Repository, repository,
                StringComparison.OrdinalIgnoreCase));
        }
        return filtered.OrderByDescending(e => e.OccurredAt)
            .Take(Math.Clamp(take, 1, 200)).ToList();
    }
}