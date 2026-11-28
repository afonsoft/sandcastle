namespace Sandcastle.Application.Extensions;

public sealed record ExtensionSummaryInfo(int McpServers, int MarketplaceInstalled, int Plugins, int EnabledPlugins, int ScmConnections, int TicketConnections);
public sealed record PluginInfo(Guid Id, Guid? OrgId, string OwnerUserId, string Name, string Version, string ManifestJson, string[] Permissions, bool Enabled, string? RollbackVersion, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PluginUpsert(string Name, string Version, string ManifestJson, string[]? Permissions, Guid? OrgId);
public sealed record IntegrationTestResult(string Provider, bool Ok, string Detail);
public interface IExtensionHubService
{
    Task<ExtensionSummaryInfo> SummaryAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(Guid userId, CancellationToken ct);
    Task<PluginInfo> UpsertPluginAsync(Guid? id, PluginUpsert request, Guid userId, CancellationToken ct);
    Task<bool> SetPluginEnabledAsync(Guid id, bool enabled, Guid userId, CancellationToken ct);
    Task<IntegrationTestResult> TestIntegrationAsync(string provider, Guid userId, CancellationToken ct);
}