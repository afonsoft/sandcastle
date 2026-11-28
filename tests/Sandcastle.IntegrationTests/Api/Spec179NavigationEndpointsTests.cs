using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Automations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-179 RF-001/RF-005: navigation summary + UX telemetry.</summary>
public sealed class Spec179NavigationEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-nav-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec179NavigationEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-nav-ws-{Guid.NewGuid():N}"));
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
    public async Task Navigation_Summary_Counts_Attention_Running_And_Automations()
    {
        var session = await _http.GetFromJsonAsync<JsonElement>(
            "/api/v1/auth/session");
        var userId = session.GetProperty("user").GetProperty("id").GetGuid()
            .ToString();
        var baselineResponse = await _http.GetAsync("/api/v1/navigation/summary");
        baselineResponse.EnsureSuccessStatusCode();
        var baselineExtensions = (await baselineResponse.Content
                .ReadFromJsonAsync<JsonElement>())
            .GetProperty("extension_count").GetInt32();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var runningId = Guid.NewGuid();
            db.Conversations.Add(new ConversationRecord
            {
                Id = runningId,
                Title = "running",
                ExecutionStatus = "Running",
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Events.Add(new EventRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = runningId,
                Kind = "TokenEvent",
                Source = "agent",
                TimestampTicks = now.UtcTicks,
                Payload = """{"kind":"TokenEvent","model":"gpt-5","prompt_tokens":10,"completion_tokens":5,"cost_usd":0.01}""",
            });
            db.Conversations.Add(new ConversationRecord
            {
                Id = Guid.NewGuid(),
                Title = "attention",
                ExecutionStatus = "Finished",
                NeedsAttentionKind = "confirmation",
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.InstalledSkills.Add(new InstalledSkillRecord
            {
                Id = Guid.NewGuid(),
                Scope = "system",
                EntryId = "visible-extension",
                Version = "1.0.0",
                Enabled = true,
                InstalledAt = now,
                InstalledAtTicks = now.UtcTicks,
            });
            db.InstalledSkills.Add(new InstalledSkillRecord
            {
                Id = Guid.NewGuid(),
                Scope = "user",
                ScopeId = Guid.NewGuid().ToString(),
                EntryId = "other-user-extension",
                Version = "1.0.0",
                Enabled = true,
                InstalledAt = now,
                InstalledAtTicks = now.UtcTicks,
            });
            db.Automations.Add(new AutomationRecord
            {
                Id = Guid.NewGuid(),
                CreatedByUserId = userId,
                Name = "nightly",
                Prompt = "run",
                Kind = "task",
                ScheduleKind = "interval",
                IntervalMinutes = 60,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var response = await _http.GetAsync("/api/v1/navigation/summary");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("attention_count").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        json.GetProperty("running_count").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        json.GetProperty("automation_count").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        json.GetProperty("factory_count").GetInt32().ShouldBe(0);
        json.GetProperty("extension_count").GetInt32()
            .ShouldBe(baselineExtensions + 1);

        var searchResponse = await _http.GetAsync(
            "/api/v1/app-conversations/search?limit=20");
        searchResponse.StatusCode.ShouldBe(HttpStatusCode.OK,
            await searchResponse.Content.ReadAsStringAsync());
        var search = await searchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var running = search.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("title").GetString() == "running");
        running.GetProperty("cost_usd").GetDecimal().ShouldBe(0.01m);
    }


    [Fact]
    public async Task Ux_Event_Rejects_Invalid_Event_Name()
    {
        var response = await _http.PostAsJsonAsync("/api/v1/ux-events", new
        {
            event_name = "route navigation with spaces",
            route = "/conversations",
            duration_ms = 42,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ux_Event_Accepts_Navigation_Telemetry_Without_Pii_Echo()
    {
        var response = await _http.PostAsJsonAsync("/api/v1/ux-events", new
        {
            event_name = "route_navigation",
            route = "/conversations",
            duration_ms = 42,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("/conversations");
    }
}