using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Automations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Knowledge;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-170 RF-005: mention search endpoint across providers.</summary>
public sealed class Spec170MentionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-mentions-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec170MentionEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-mention-ws-{Guid.NewGuid():N}"));
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Mentions_Search_Returns_Provider_Items_And_Filters_Type()
    {
        var session = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/session");
        var userId = session.GetProperty("user").GetProperty("id").GetGuid()
            .ToString();
        var convId = Guid.NewGuid();
        var skillId = Guid.NewGuid();
        var automationId = Guid.NewGuid();
        var knowledgeId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new ConversationRecord
            {
                Id = convId,
                Title = "release fix audit",
                ExecutionStatus = "Running",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Skills.Add(new SkillRecord
            {
                Id = skillId,
                Name = "release fixer",
                Description = "fix helper",
                Content = "# fix",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Automations.Add(new AutomationRecord
            {
                Id = automationId,
                CreatedByUserId = userId,
                Name = "release fix automation",
                Prompt = "fix release",
                Kind = "task",
                ScheduleKind = "interval",
                IntervalMinutes = 30,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.KnowledgeSuggestions.Add(new KnowledgeSuggestionRecord
            {
                Id = knowledgeId,
                OrgId = null,
                Scope = "user",
                Title = "release fix knowledge",
                Content = "remember the release fix",
                Status = "approved",
            });
            await db.SaveChangesAsync();
        }

        var response = await _http.GetAsync(
            "/api/v1/mentions?q=" + Uri.EscapeDataString("release fix"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var types = json.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("type").GetString()).ToHashSet();
        types.ShouldContain("conversation");
        types.ShouldContain("skill");
        types.ShouldContain("automation");
        types.ShouldContain("knowledge");

        var filtered = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/mentions?q=release%20fix&types=skill");
        filtered.GetProperty("items").EnumerateArray()
            .All(i => i.GetProperty("type").GetString() == "skill")
            .ShouldBeTrue();
    }
}