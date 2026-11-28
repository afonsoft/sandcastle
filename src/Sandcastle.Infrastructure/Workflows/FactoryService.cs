using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Automations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Workflows;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Workflows;

/// <summary>SPEC-183 RF-002: owner/org-scoped factory CRUD and automation grouping.</summary>
public sealed class FactoryService(AppDbContext db, IAuditService? audit = null)
    : IFactoryService
{
    private async Task<FactoryInfo> ToInfoAsync(FactoryRecord r, CancellationToken ct)
    {
        var automations = await db.Automations.CountAsync(a => a.FactoryId == r.Id, ct);
        var workflows = await db.Workflows.CountAsync(w => w.FactoryId == r.Id, ct);
        return new FactoryInfo(r.Id, r.OrgId, r.OwnerUserId, r.Name,
            r.Description, automations, workflows, r.CreatedAt, r.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FactoryInfo>> ListAsync(
        Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rows = await db.Factories.AsNoTracking()
            .Where(f => f.OwnerUserId == owner)
            .OrderBy(f => f.Name).ThenBy(f => f.Id)
            .ToListAsync(ct);
        var result = new List<FactoryInfo>(rows.Count);
        foreach (var row in rows) result.Add(await ToInfoAsync(row, ct));
        return result;
    }

    /// <inheritdoc />
    public async Task<FactoryInfo?> GetAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Factories.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.OwnerUserId == owner, ct);
        return row is null ? null : await ToInfoAsync(row, ct);
    }

    /// <inheritdoc />
    public async Task<(FactoryInfo? Info, string? Error)> CreateAsync(
        FactoryUpsert request, Guid ownerUserId, Guid? orgId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return (null, "name required");
        var now = DateTimeOffset.UtcNow;
        var row = new FactoryRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerUserId = ownerUserId.ToString(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Factories.Add(row);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("factory_create", ownerUserId.ToString(), orgId,
                "factory", row.Id.ToString(), metadata: new { row.Name },
                cancellationToken: CancellationToken.None);
        }
        return (await ToInfoAsync(row, ct), null);
    }

    /// <inheritdoc />
    public async Task<(FactoryInfo? Info, string? Error)> UpdateAsync(
        Guid id, FactoryUpsert request, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Factories.FirstOrDefaultAsync(
            f => f.Id == id && f.OwnerUserId == owner, ct);
        if (row is null) return (null, "not found");
        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return (null, "name required");
            row.Name = request.Name.Trim();
        }
        if (request.Description is not null) row.Description = request.Description.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("factory_update", owner, row.OrgId,
                "factory", row.Id.ToString(), metadata: new { row.Name },
                cancellationToken: CancellationToken.None);
        }
        return (await ToInfoAsync(row, ct), null);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var row = await db.Factories.FirstOrDefaultAsync(
            f => f.Id == id && f.OwnerUserId == owner, ct);
        if (row is null) return false;
        await db.Automations.Where(a => a.FactoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.FactoryId, (Guid?)null), ct);
        await db.Workflows.Where(w => w.FactoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.FactoryId, (Guid?)null), ct);
        db.Factories.Remove(row);
        await db.SaveChangesAsync(ct);
        if (audit is not null)
        {
            await audit.RecordAsync("factory_delete", owner, row.OrgId,
                "factory", row.Id.ToString(), metadata: new { row.Name },
                cancellationToken: CancellationToken.None);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationInfo>> ListAutomationsAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        if (await GetAsync(id, ownerUserId, ct) is null) return [];
        var rows = await db.Automations.AsNoTracking()
            .Where(a => a.FactoryId == id)
            .OrderBy(a => a.Name).ThenBy(a => a.Id)
            .ToListAsync(ct);
        return rows.Select(r => new AutomationInfo(
            r.Id, r.OrgId, r.CreatedByUserId, r.Name, r.Prompt, r.LlmModel,
            r.SelectedRepository, r.ScheduleKind, r.ScheduleTimeUtc,
            r.IntervalMinutes, r.Weekday, r.Enabled, r.NextRunAt, r.LastRunAt,
            r.CreatedAt, r.UpdatedAt, r.Kind, r.ScanConfigJson,
            r.ConsecutiveFailures, false, r.MisfirePolicy, r.TimeZoneId,
            r.CronExpression, r.ApprovalPreset, FactoryId: r.FactoryId)).ToList();
    }
}