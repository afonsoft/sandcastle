using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec190LifecycleEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-lifecycle-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory; private readonly HttpClient _http;
    public Spec190LifecycleEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseSetting("Database:Provider", "Sqlite"); b.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10"); b.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}"); });
        _http = TestAuth.Login(_factory);
    }
    public void Dispose() { _http.Dispose(); _factory.Dispose(); if (File.Exists(_dbPath)) File.Delete(_dbPath); }
    [Fact]
    public async Task Lifecycle_Admin_Jobs_And_Interaction_Are_Exposed()
    {
        var jobs = await _http.GetAsync("/api/v1/admin/lifecycle/jobs");
        jobs.StatusCode.ShouldBe(HttpStatusCode.OK);
        var run = await _http.PostAsync("/api/v1/admin/lifecycle/jobs/integration-resync", null);
        run.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = await _http.PostAsJsonAsync("/api/v1/admin/lifecycle/provisioning", new { email = "user@example.com", org_id = (Guid?)null, quotas_json = (string?)null, feature_flags_json = (string?)null });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var provisioning = await created.Content.ReadFromJsonAsync<JsonElement>();
        var provisioningId = provisioning.GetProperty("id").GetGuid();
        (await _http.PostAsync($"/api/v1/admin/lifecycle/provisioning/{provisioningId}/invite", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.PostAsync($"/api/v1/admin/lifecycle/provisioning/{provisioningId}/approve", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var interaction = await _http.PostAsJsonAsync("/api/v1/lifecycle/interactions", new { @event = "onboarding_started" });
        interaction.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/lifecycle/jobs")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var listed = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/admin/lifecycle/jobs");
        listed!.ShouldContain(x => x.GetProperty("job_key").GetString() == "integration-resync");
    }
}