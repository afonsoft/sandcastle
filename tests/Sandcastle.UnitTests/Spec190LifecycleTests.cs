using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Infrastructure.Lifecycle;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.UnitTests;

public sealed class Spec190LifecycleTests
{
    private sealed class NullHttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private static (LifecycleService Svc, SqliteConnection Conn) Make()
    {
        var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).UseSnakeCaseNamingConvention().Options;
        var db = new AppDbContext(opts);
        db.Database.ExecuteSqlRaw("""
CREATE TABLE lifecycle_provisioning_requests(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,email TEXT NOT NULL,normalized_email TEXT NOT NULL,status TEXT NOT NULL,quotas_json TEXT NOT NULL,feature_flags_json TEXT NOT NULL,invite_count INTEGER NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,approved_at TEXT NULL,onboarded_at TEXT NULL);
CREATE TABLE integration_outbox_events(id TEXT NOT NULL PRIMARY KEY,org_id TEXT NULL,provider TEXT NOT NULL,event_type TEXT NOT NULL,payload_json TEXT NOT NULL,target_url TEXT NULL,status TEXT NOT NULL,attempts INTEGER NOT NULL,max_attempts INTEGER NOT NULL,next_retry_at TEXT NULL,last_error TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,delivered_at TEXT NULL);
CREATE TABLE lifecycle_job_runs(id TEXT NOT NULL PRIMARY KEY,job_key TEXT NOT NULL,status TEXT NOT NULL,details_json TEXT NOT NULL,created_at TEXT NOT NULL,completed_at TEXT NULL);
""");
        return (new LifecycleService(db, new NullHttpFactory()), conn);
    }
    [Fact]
    public async Task Provisioning_Deduplicates_Email_And_Transitions()
    {
        var (svc, conn) = Make(); using var _ = conn;
        var a = await svc.RequestProvisioningAsync(new("User@Example.com", null, null, null), "admin", default);
        var b = await svc.RequestProvisioningAsync(new("user@example.com", null, null, null), "admin", default);
        b.Id.ShouldBe(a.Id); b.InviteCount.ShouldBe(2);
        (await svc.InviteProvisioningAsync(a.Id, "admin", default))!.Status.ShouldBe("invited");
        (await svc.ApproveProvisioningAsync(a.Id, "admin", default))!.Status.ShouldBe("approved");
        (await svc.MarkOnboardedAsync(a.Id, "admin", default))!.Status.ShouldBe("onboarded");
    }
    [Fact]
    public async Task Outbox_Invalid_Target_DeadLetters_And_Can_Replay()
    {
        var (svc, conn) = Make(); using var _ = conn;
        var row = await svc.EnqueueEventAsync(new("scm", "pull_request", "{}", null, null, 1), "admin", default);
        (await svc.ProcessDueEventsAsync(10, default)).ShouldBe(1);
        (await svc.ListOutboxAsync("dead_letter", default)).Single().Id.ShouldBe(row.Id);
        var replay = await svc.ReplayDeadLetterAsync(row.Id, "admin", default);
        replay.ShouldNotBeNull(); replay!.Status.ShouldBe("pending");
    }
}