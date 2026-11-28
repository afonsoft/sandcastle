using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;
using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec185ObservabilityEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-obs-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec185ObservabilityEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose(); _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Analytics_Budgets_Export_And_Trace_Are_Available()
    {
        var conversationId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new ConversationRecord
            {
                Id = conversationId, Title = "trace run", ExecutionStatus = "Running",
                CreatedByUserId = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Events.Add(new EventRecord
            {
                Id = "obs-1", ConversationId = conversationId, Kind = "TokenEvent", Source = "agent",
                TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
                Payload = """{"kind":"TokenEvent","model":"gpt-5","prompt_tokens":4,"completion_tokens":3,"cost_usd":0.01,"latency_ms":50}""",
            });
            await db.SaveChangesAsync();
        }

        var summary = await _http.GetFromJsonAsync<JsonElement>("/api/v1/admin/analytics/summary?days=30");
        summary.GetProperty("total_tokens").GetInt64().ShouldBe(7);
        (await _http.GetAsync("/api/v1/admin/analytics/rankings?by=conversation")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var csv = await _http.GetStringAsync("/api/v1/admin/analytics/export");
        csv.ShouldContain("trace run");
        (await _http.GetAsync("/api/v1/admin/budgets")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var reconcile = await _http.PostAsync("/api/v1/admin/budgets/reconcile", null);
        reconcile.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var trace = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{conversationId}/trace");
        trace.GetProperty("total_tokens").GetInt64().ShouldBe(7);
        trace.GetProperty("llm_calls").GetInt32().ShouldBe(1);
    }
}
