using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec188ChatUxEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-chatux-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory; private readonly HttpClient _http;
    public Spec188ChatUxEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseSetting("Database:Provider", "Sqlite"); b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10"); b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}"); });
        _http = TestAuth.Login(_factory);
    }
    public void Dispose() { _http.Dispose(); _factory.Dispose(); if (File.Exists(_dbPath)) File.Delete(_dbPath); }
    [Fact]
    public async Task Branches_And_Pr_Status_Are_Available()
    {
        var id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Select(u => u.Id).FirstAsync();
            db.Conversations.Add(new ConversationRecord { Id = id, Title = "main", ExecutionStatus = "Ready", CreatedByUserId = userId.ToString(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, PullRequestUrl = "https://example.test/pr/1", PullRequestNumber = 1, PullRequestState = "open" });
            await db.SaveChangesAsync();
        }
        var branches = await _http.GetAsync($"/api/v1/conversations/{id}/branches");
        branches.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pr = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}/pr-status");
        pr.GetProperty("pull_request_number").GetInt32().ShouldBe(1);
    }
}