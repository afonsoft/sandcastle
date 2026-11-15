using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Outpost;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// <see cref="IOutpostRouter"/> — resolution order (SPEC-153 RF-002):
/// 1. <c>conversations.outpost_id</c> pin → that outpost (required).
/// 2. org <c>data_residency=internal-only</c> (SPEC-149 rules doc) →
///    any online org outpost (required).
/// 3. policy — org <c>settings_json["outpost_policy"]</c> overriding
///    <c>Outpost:DefaultPolicy</c>:
///    <c>cloud</c> → none · <c>prefer-outpost</c> → online or cloud ·
///    <c>outpost-only</c> → online or fail.
/// Singleton-safe (db via scope factory) — the engine is a singleton.
/// </summary>
public sealed class OutpostRouter(
    IServiceScopeFactory scopeFactory,
    IOptions<OutpostOptions> options,
    IOutpostTaskDispatcher dispatcher) : IOutpostRouter
{
    /// <summary>Org settings key holding the per-org policy override.</summary>
    public const string OrgPolicyKey = "outpost_policy";

    /// <inheritdoc />
    public async Task<OutpostRouteDecision> ResolveAsync(
        Guid? conversationId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return new OutpostRouteDecision(null, "disabled", false);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid? orgId = null;
        // 1) pin
        if (conversationId is { } convId)
        {
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == convId)
                .Select(c => new { c.OrgId, c.OutpostId })
                .FirstOrDefaultAsync(ct);
            orgId = conv?.OrgId;
            if (conv?.OutpostId is { } pinned)
            {
                // Pin vence tudo — Required=true mesmo offline (o caller
                // falha rápido em vez de vazar execução p/ cloud).
                return new OutpostRouteDecision(pinned, "pin", true);
            }
        }

        // 2) residency — internal-only orgs must execute on-prem.
        var residency = await ReadResidencyAsync(scope, db, orgId, ct);
        if (residency)
        {
            var online = await PickOnlineAsync(orgId, ct);
            return new OutpostRouteDecision(online, "residency", true);
        }

        // 3) policy
        var policy = (await ReadOrgPolicyAsync(db, orgId, ct)
            ?? options.Value.DefaultPolicy ?? OutpostPolicies.Cloud)
            .Trim().ToLowerInvariant();
        return policy switch
        {
            OutpostPolicies.OutpostOnly => new OutpostRouteDecision(
                await PickOnlineAsync(orgId, ct), "policy:outpost-only", true),
            OutpostPolicies.PreferOutpost => new OutpostRouteDecision(
                await PickOnlineAsync(orgId, ct), "policy:prefer-outpost", false),
            _ => new OutpostRouteDecision(null, "policy:cloud", false),
        };
    }

    /// <inheritdoc />
    public Task<Guid?> PickOnlineAsync(Guid? orgId, CancellationToken ct)
    {
        var online = dispatcher.OnlineOutposts(orgId);
        if (online.Count == 0)
        {
            return Task.FromResult<Guid?>(null);
        }

        // Least-loaded, ties broken by lowest id — deterministic so the
        // service pin and the engine dispatch agree on the choice.
        var pick = online
            .OrderBy(id => dispatcher.RunningTasks(id))
            .ThenBy(id => id)
            .First();
        return Task.FromResult<Guid?>(pick);
    }

    private static async Task<bool> ReadResidencyAsync(
        AsyncServiceScope scope, AppDbContext db, Guid? orgId,
        CancellationToken ct)
    {
        if (orgId is null)
        {
            return false;
        }

        var rules = scope.ServiceProvider
            .GetService<IRoutingRulesService>();
        if (rules is null)
        {
            return false;
        }

        var doc = await rules.GetAsync(orgId, ct);
        return string.Equals(doc?.DataResidency, "internal-only",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadOrgPolicyAsync(
        AppDbContext db, Guid? orgId, CancellationToken ct)
    {
        if (orgId is null)
        {
            return null;
        }

        var json = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.SettingsJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var map = JsonSerializer
                .Deserialize<Dictionary<string, string>>(json);
            return map is not null
                && map.TryGetValue(OrgPolicyKey, out var v)
                ? v
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
