using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Identity;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Identity;

/// <summary>SPEC-158 RF-001: PAT hash-only, escopos, expiry, revogação.</summary>
public sealed class Spec158PatTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly PersonalAccessTokenService _svc;
    private readonly List<string> _audit = [];

    public Spec158PatTests()
    {
        _conn.Open();
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();
        _svc = new PersonalAccessTokenService(_db, new Audit(_audit));
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task Create_Stores_Hash_Only_And_Returns_Raw_Once()
    {
        var userId = Guid.NewGuid();
        var (id, raw) = await _svc.CreateAsync(userId, "ci",
            ["read"], null, CancellationToken.None);

        raw.ShouldStartWith("sc_pat_");
        var rec = await _db.PersonalAccessTokens.FindAsync([id]);
        rec.ShouldNotBeNull();
        rec.TokenHash.ShouldBe(PersonalAccessTokenService.Hash(raw));
        rec.TokenHash.ShouldNotContain(raw);
        rec.ScopesJson.ShouldContain("read");
        _audit.ShouldContain("pat.created");
    }

    [Fact]
    public async Task Validate_Resolves_User_And_Scopes_And_Throttles_LastUsed()
    {
        var userId = Guid.NewGuid();
        var (id, raw) = await _svc.CreateAsync(userId, "ci",
            ["read", "write"], null, CancellationToken.None);

        var r1 = await _svc.ValidateAsync(raw, CancellationToken.None);
        r1.ShouldNotBeNull();
        r1.UserId.ShouldBe(userId);
        r1.TokenId.ShouldBe(id);
        r1.Scopes.ShouldBe(["read", "write"]);

        var firstUsed = (await _db.PersonalAccessTokens.FindAsync([id]))!
            .LastUsedAt;
        firstUsed.ShouldNotBeNull();

        // Throttle 60s: segundo validate não muda last_used_at.
        await _svc.ValidateAsync(raw, CancellationToken.None);
        (await _db.PersonalAccessTokens.FindAsync([id]))!.LastUsedAt
            .ShouldBe(firstUsed);
        _audit.ShouldContain("pat.used");
    }

    [Fact]
    public async Task Validate_Rejects_Unknown_Expired_And_Revoked()
    {
        var userId = Guid.NewGuid();
        (await _svc.ValidateAsync("sc_pat_nonexistent",
            CancellationToken.None)).ShouldBeNull();

        var (_, expired) = await _svc.CreateAsync(userId, "old",
            ["read"], DateTimeOffset.UtcNow.AddDays(-1),
            CancellationToken.None);
        (await _svc.ValidateAsync(expired, CancellationToken.None))
            .ShouldBeNull();

        var (id, raw) = await _svc.CreateAsync(userId, "gone",
            ["read"], null, CancellationToken.None);
        (await _svc.RevokeAsync(userId, id, CancellationToken.None))
            .ShouldBeTrue();
        (await _svc.ValidateAsync(raw, CancellationToken.None))
            .ShouldBeNull();
        _audit.ShouldContain("pat.revoked");
    }

    [Fact]
    public async Task Revoke_Only_Own_Tokens()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var (id, _) = await _svc.CreateAsync(owner, "mine",
            ["read"], null, CancellationToken.None);
        (await _svc.RevokeAsync(other, id, CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public void PasswordHasher_Roundtrip_And_Rejects_Tamper()
    {
        var hash = LocalPasswordHasher.Hash("correct horse battery!");
        hash.ShouldStartWith("pbkdf2-sha256$");
        LocalPasswordHasher.Verify("correct horse battery!", hash)
            .ShouldBeTrue();
        LocalPasswordHasher.Verify("wrong password!!", hash).ShouldBeFalse();
        LocalPasswordHasher.Verify("correct horse battery!", "junk")
            .ShouldBeFalse();
        LocalPasswordHasher.Verify("x", null).ShouldBeFalse();
    }

    private sealed class Audit(List<string> sink) : IAuditService
    {
        public Task RecordAsync(
            string action, string? actorUserId = null, Guid? orgId = null,
            string? targetType = null, string? targetId = null,
            string? ip = null, string? userAgent = null,
            object? metadata = null, CancellationToken cancellationToken = default)
        {
            sink.Add(action);
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry> Items, int Total)>(
                ([], 0));
    }
}