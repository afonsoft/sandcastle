using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec187ExtensionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-ext-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory; private readonly HttpClient _http;
    public Spec187ExtensionEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseSetting("Database:Provider", "Sqlite"); b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10"); b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}"); });
        _http = TestAuth.Login(_factory);
    }
    public void Dispose() { _http.Dispose(); _factory.Dispose(); if (File.Exists(_dbPath)) File.Delete(_dbPath); }
    [Fact]
    public async Task Summary_Plugins_And_Integration_Test_Are_Available()
    {
        (await _http.GetAsync("/api/v1/extensions/summary")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = await _http.PostAsJsonAsync("/api/v1/plugins", new { name = "hello", version = "1.0.0", manifest_json = "{}", permissions = new[] { "read_workspace" }, org_id = (Guid?)null });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var test = await _http.PostAsJsonAsync("/api/v1/integrations/github/test", new { });
        test.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean().ShouldBeTrue();
    }
}
