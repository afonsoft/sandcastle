using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-169 RF-002/003: plan artifact API and execute gate.</summary>
public sealed class Spec169PlanArtifactEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-planapi-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec169PlanArtifactEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:DefaultImage", "bash:5");
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.UseSetting("Sandbox:WorkspaceRoot",
                Path.Combine(Path.GetTempPath(), $"sc-plan-ws-{Guid.NewGuid():N}"));
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
    public async Task Plan_Put_Decide_And_Execute_Gate()
    {
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new ConversationRecord
            {
                Id = id,
                ExecutionStatus = "Running",
                Mode = "plan",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var put = await _http.PutAsJsonAsync(
            $"/api/v1/app-conversations/{id}/plan", new
            {
                items = new[]
                {
                    new { title = "edit", files = new[] { "a.cs" }, rationale = (string?)"why" },
                    new { title = "test", files = new[] { "t.cs" }, rationale = (string?)null },
                },
            });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var plan = await put.Content.ReadFromJsonAsync<JsonElement>();
        plan.GetProperty("version").GetInt32().ShouldBe(1);
        var first = plan.GetProperty("items")[0].GetProperty("id").GetGuid();
        var second = plan.GetProperty("items")[1].GetProperty("id").GetGuid();

        (await _http.PostAsync($"/api/v1/app-conversations/{id}/plan/execute", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        foreach (var item in new[] { first, second })
        {
            (await _http.PostAsJsonAsync(
                $"/api/v1/app-conversations/{id}/plan/items/{item}/decision",
                new { decision = "approve", comment = "ok" }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var execute = await _http.PostAsync(
            $"/api/v1/app-conversations/{id}/plan/execute", null);
        execute.StatusCode.ShouldBe(HttpStatusCode.OK);
        var executed = await execute.Content.ReadFromJsonAsync<JsonElement>();
        executed.GetProperty("executed_at").GetString().ShouldNotBeNullOrEmpty();
    }
}