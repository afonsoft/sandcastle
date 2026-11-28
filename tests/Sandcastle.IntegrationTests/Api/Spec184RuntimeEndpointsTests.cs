using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec184RuntimeEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-runtime-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec184RuntimeEndpointsTests()
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
    public async Task Runtime_Fleet_Pools_Images_And_Resize_Are_Admin_Available()
    {
        var fleet = await _http.GetFromJsonAsync<JsonElement>("/api/v1/admin/runtime/fleet");
        fleet.TryGetProperty("capacity", out _).ShouldBeTrue();
        fleet.TryGetProperty("items", out _).ShouldBeTrue();
        (await _http.GetAsync("/api/v1/admin/runtime/pools")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.GetAsync("/api/v1/admin/runtime/images")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var invalid = await _http.PostAsJsonAsync("/api/v1/admin/runtime/resize", new { image = "agent", desired = 99 });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var resize = await _http.PostAsJsonAsync("/api/v1/admin/runtime/resize", new { image = "agent", desired = 0 });
        resize.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Runtime_Operations_Reject_Missing_Targets()
    {
        var missing = await _http.PostAsJsonAsync("/api/v1/admin/runtime/terminate", new { id = Guid.NewGuid() });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var recycle = await _http.PostAsJsonAsync("/api/v1/admin/runtime/recycle", new { id = Guid.NewGuid() });
        recycle.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}