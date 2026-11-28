using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-183: factories/workflows API contracts, owner isolation and event center.</summary>
public sealed class Spec183WorkflowEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-wf-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec183WorkflowEndpointsTests()
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
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Factory_Crud_And_Automation_Grouping_Work()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/factories", new
        {
            name = "Delivery factory",
            description = "group",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var factory = await create.Content.ReadFromJsonAsync<JsonElement>();
        var factoryId = factory.GetProperty("id").GetGuid();

        var automation = await _http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "grouped",
            prompt = "hello",
            schedule_kind = "once",
            next_run_at = DateTimeOffset.UtcNow.AddHours(1),
            factory_id = factoryId,
        });
        automation.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await automation.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("factory_id").GetGuid().ShouldBe(factoryId);

        var automations = await _http.GetFromJsonAsync<JsonElement[]>(
            $"/api/v1/factories/{factoryId}/automations");
        automations!.Single().GetProperty("name").GetString().ShouldBe("grouped");

        var patch = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/v1/factories/{factoryId}")
        {
            Content = JsonContent.Create(new { name = "Renamed" }),
        });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("name").GetString().ShouldBe("Renamed");

        (await _http.DeleteAsync($"/api/v1/factories/{factoryId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var automationAfter = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/automations");
        automationAfter!.Single().GetProperty("factory_id").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Workflow_Crud_Versioning_Run_Inputs_And_Events_Work()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/workflows", new
        {
            name = "Controlled delivery",
            autonomous_delivery = false,
            definition_json = """
            {"inputs":[{"name":"repository","type":"string","required":true}],
             "steps":[{"name":"human_gate","type":"approval"}],
             "retry":{"max_attempts":2,"max_steps":4}}
            """,
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var workflow = await create.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = workflow.GetProperty("id").GetGuid();
        workflow.GetProperty("version").GetInt32().ShouldBe(1);

        var patch = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/v1/workflows/{workflowId}")
        {
            Content = JsonContent.Create(new { description = "v2" }),
        });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32().ShouldBe(2);

        var invalidStart = await _http.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/runs", new
            {
                inputs = new { repository = 42 },
            });
        invalidStart.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var start = await _http.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/runs", new
            {
                inputs = new { repository = "owner/repo" },
            });
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var run = await start.Content.ReadFromJsonAsync<JsonElement>();
        var runId = run.GetProperty("id").GetGuid();
        run.GetProperty("status").GetString().ShouldBe("waiting_approval");
        run.GetProperty("audit").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);

        var duplicate = await _http.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/runs", new
            {
                inputs = new { repository = "owner/repo" },
            });
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var reject = await _http.PostAsJsonAsync(
            $"/api/v1/workflows/runs/{runId}/approval", new
            {
                approved = false,
                comment = "not now",
            });
        reject.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reject.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().ShouldBe("rejected");

        var events = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/automation-events?take=20");
        events!.AsEnumerable().ShouldContain(e => e.GetProperty("source").GetString() == "workflow");
        events!.ShouldContain(e => e.GetProperty("deep_link").GetString()!
            .StartsWith("/workflows/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Templates_Exist_And_Invalid_Factory_Is_Rejected()
    {
        var templates = await _http.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/automation-templates");
        templates!.Length.ShouldBeGreaterThanOrEqualTo(4);
        templates.ShouldContain(t => t.GetProperty("id").GetString() == "autonomous-delivery");

        var badAutomation = await _http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "bad factory",
            prompt = "hello",
            schedule_kind = "once",
            next_run_at = DateTimeOffset.UtcNow.AddHours(1),
            factory_id = Guid.NewGuid(),
        });
        badAutomation.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}