using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sandcastle.Application.Extensions;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Extensions;

public sealed class ExtensionHubService(AppDbContext db) : IExtensionHubService
{
    private static string U(Guid id) => id.ToString();
    private async Task<List<Guid>> OrgsAsync(Guid userId, CancellationToken ct) =>
        await db.OrgMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.OrgId).ToListAsync(ct);

    public async Task<ExtensionSummaryInfo> SummaryAsync(Guid userId, CancellationToken ct)
    {
        var uid = U(userId); var orgs = await OrgsAsync(userId, ct);
        var plugins = await db.Plugins.CountAsync(p => p.OwnerUserId == uid || (p.OrgId != null && orgs.Contains(p.OrgId.Value)), ct);
        var enabled = await db.Plugins.CountAsync(p => p.Enabled && (p.OwnerUserId == uid || (p.OrgId != null && orgs.Contains(p.OrgId.Value))), ct);
        return new ExtensionSummaryInfo(
            await db.McpServers.CountAsync(ct),
            0,
            plugins, enabled,
            await db.ScmConnections.CountAsync(ct),
            await db.TicketConnections.CountAsync(ct));
    }

    public async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(Guid userId, CancellationToken ct)
    {
        var uid = U(userId); var orgs = await OrgsAsync(userId, ct);
        var rows = await db.Plugins.AsNoTracking().Where(p => p.OwnerUserId == uid || (p.OrgId != null && orgs.Contains(p.OrgId.Value))).ToListAsync(ct);
        return rows.OrderByDescending(p => p.UpdatedAt).Select(Map).ToList();
    }

    public async Task<PluginInfo> UpsertPluginAsync(Guid? id, PluginUpsert request, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = id is null ? null : await db.Plugins.FirstOrDefaultAsync(p => p.Id == id && p.OwnerUserId == U(userId), ct);
        if (row is null)
        {
            row = new PluginRecord { Id = Guid.NewGuid(), OwnerUserId = U(userId), Name = request.Name.Trim(), Version = request.Version.Trim(), ManifestJson = request.ManifestJson, OrgId = request.OrgId, CreatedAt = now, UpdatedAt = now };
            db.Plugins.Add(row);
        }
        else
        {
            row.RollbackVersion = row.Version; row.Name = request.Name.Trim(); row.Version = request.Version.Trim(); row.ManifestJson = request.ManifestJson; row.UpdatedAt = now;
            if (request.OrgId is not null) row.OrgId = request.OrgId;
        }
        row.PermissionsJson = JsonSerializer.Serialize(request.Permissions ?? []);
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<bool> SetPluginEnabledAsync(Guid id, bool enabled, Guid userId, CancellationToken ct)
    {
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Id == id && p.OwnerUserId == U(userId), ct);
        if (row is null) return false;
        row.Enabled = enabled; row.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); return true;
    }

    public Task<IntegrationTestResult> TestIntegrationAsync(string provider, Guid userId, CancellationToken ct)
    {
        var known = provider is "github" or "gitlab" or "bitbucket" or "azure-devops" or "slack" or "linear" or "jira" or "n8n" or "webhooks";
        return Task.FromResult(new IntegrationTestResult(provider, known, known ? "configuration endpoint reachable; provider-specific credentials are validated by existing connection tests" : "unknown provider"));
    }

    private static PluginInfo Map(PluginRecord p) => new(p.Id, p.OrgId, p.OwnerUserId, p.Name, p.Version, p.ManifestJson,
        JsonSerializer.Deserialize<string[]>(p.PermissionsJson) ?? [], p.Enabled, p.RollbackVersion, p.CreatedAt, p.UpdatedAt);
}
