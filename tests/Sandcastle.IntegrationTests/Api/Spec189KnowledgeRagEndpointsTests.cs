using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec189KnowledgeRagEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-rag-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory; private readonly HttpClient _http;
    public Spec189KnowledgeRagEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseSetting("Database:Provider", "Sqlite"); b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10"); b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}"); });
        _http = TestAuth.Login(_factory);
    }
    public void Dispose() { _http.Dispose(); _factory.Dispose(); if (File.Exists(_dbPath)) File.Delete(_dbPath); }
    [Fact]
    public async Task Collections_Documents_And_Query_RoundTrip()
    {
        var created = await _http.PostAsJsonAsync("/api/v1/knowledge/collections", new { name = "docs", repo_path = "owner/repo", description = (string?)null, org_id = (Guid?)null });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var collection = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = collection.GetProperty("id").GetGuid();
        var doc = await _http.PostAsJsonAsync($"/api/v1/knowledge/collections/{id}/documents", new { title = "runbook", source_uri = "repo://runbook.md", content = "deploy rollback runbook" });
        doc.StatusCode.ShouldBe(HttpStatusCode.Created);
        var query = await _http.PostAsJsonAsync("/api/v1/knowledge/query", new { query = "rollback", collection_id = id, repo_path = (string?)null, take = 5, include_web = false });
        query.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await query.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("citations")[0].GetProperty("excerpt").GetString()!.ShouldContain("rollback");
    }
}