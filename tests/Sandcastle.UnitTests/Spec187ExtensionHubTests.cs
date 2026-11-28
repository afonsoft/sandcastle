using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sandcastle.Infrastructure.Extensions;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.UnitTests;

public sealed class Spec187ExtensionHubTests
{
    [Fact]
    public async Task Plugin_Upsert_Enable_And_Rollback_Metadata_RoundTrip()
    {
        var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).UseSnakeCaseNamingConvention().Options;
        var db = new AppDbContext(opts);
        db.Database.ExecuteSqlRaw("""
CREATE TABLE org_members(org_id TEXT NOT NULL,user_id TEXT NOT NULL,role TEXT NOT NULL,source TEXT NOT NULL,usage_tier_id TEXT NULL,created_at TEXT NOT NULL);
CREATE TABLE plugins(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,owner_user_id TEXT NOT NULL,name TEXT NOT NULL,version TEXT NOT NULL,manifest_json TEXT NOT NULL,permissions_json TEXT NOT NULL,enabled INTEGER NOT NULL,rollback_version TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE mcp_servers(id TEXT NOT NULL PRIMARY KEY); CREATE TABLE scm_connections(id TEXT NOT NULL PRIMARY KEY); CREATE TABLE ticket_connections(id TEXT NOT NULL PRIMARY KEY);
""");
        var svc = new ExtensionHubService(db); var user = Guid.NewGuid();
        var p = await svc.UpsertPluginAsync(null, new("hello", "1.0.0", "{}", ["read_workspace"], null), user, default);
        p.Enabled.ShouldBeFalse();
        p = await svc.UpsertPluginAsync(p.Id, new("hello", "1.1.0", "{}", ["read_workspace"], null), user, default);
        p.RollbackVersion.ShouldBe("1.0.0");
        (await svc.SetPluginEnabledAsync(p.Id, true, user, default)).ShouldBeTrue();
        (await svc.ListPluginsAsync(user, default)).Single().Enabled.ShouldBeTrue();
        (await svc.TestIntegrationAsync("github", user, default)).Ok.ShouldBeTrue();
    }
}
