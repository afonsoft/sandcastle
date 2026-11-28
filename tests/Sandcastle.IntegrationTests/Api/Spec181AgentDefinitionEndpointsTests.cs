using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-181 RF-002/RF-003: agent definition catalog and versioned prompt packs.</summary>
public sealed class Spec181AgentDefinitionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-agentdef-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec181AgentDefinitionEndpointsTests()
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
    public async Task AgentDefinitions_Crud_Validate_And_Redact_Instructions()
    {
        (await _http.PostAsJsonAsync("/api/v1/agent-definitions", new
        {
            name = "bad",
            description = "bad",
            instructions = "bad",
            mode = "invalid",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var create = await _http.PostAsJsonAsync("/api/v1/agent-definitions", new
        {
            name = "repo-reviewer",
            description = "reviews repository changes",
            instructions = "Use care. Token {{secret:PRIVATE_TOKEN}} must stay hidden.",
            tools = new[] { "file_read" },
            model_profile = "review-fast",
            skills = new[] { "code-review" },
            mode = "plan",
            permission_preset = "read-only",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetGuid();
        doc.GetProperty("instructions").GetString()!.ShouldNotContain("PRIVATE_TOKEN");
        doc.GetProperty("model_profile").GetString().ShouldBe("review-fast");

        var validate = await _http.PostAsync($"/api/v1/agent-definitions/{id}/validate", null);
        validate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var validation = await validate.Content.ReadFromJsonAsync<JsonElement>();
        validation.GetProperty("valid").GetBoolean().ShouldBeTrue();

        (await _http.PutAsJsonAsync($"/api/v1/agent-definitions/{id}", new
        {
            tools = new[] { "definitely_missing_tool" },
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var invalid = await _http.PostAsync($"/api/v1/agent-definitions/{id}/validate", null);
        var invalidDoc = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        invalidDoc.GetProperty("valid").GetBoolean().ShouldBeFalse();
        invalidDoc.GetProperty("unknown_tools").ToString().ShouldContain("definitely_missing_tool");
        (await _http.PutAsJsonAsync($"/api/v1/agent-definitions/{id}", new
        {
            tools = new[] { "file_read" },
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var update = await _http.PutAsJsonAsync($"/api/v1/agent-definitions/{id}", new
        {
            description = "updated reviewer",
            enabled = false,
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-definitions");
        list!.ShouldContain(x => x.GetProperty("name").GetString() == "explore"
            && x.GetProperty("persisted").GetBoolean() == false);
        list!.ShouldNotContain(x => x.GetProperty("name").GetString() == "repo-reviewer");

        (await _http.DeleteAsync($"/api/v1/agent-definitions/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PromptPacks_Version_Compare_Rollback_And_Audit_Safety()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/prompt-packs", new
        {
            name = "default-cloud",
            base_prompt = "Base v1 {{secret:PROMPT_SECRET}}",
            harness_rules = "Harness v1",
            plan_mode_prompt = "Plan v1",
            build_mode_prompt = "Build v1",
            advisor_prompt = "Advisor v1",
            permission_judge_prompt = "Judge v1",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var packId = created.GetProperty("pack_id").GetGuid();
        created.GetProperty("base_prompt").GetString()!.ShouldNotContain("PROMPT_SECRET");

        var update = await _http.PutAsJsonAsync($"/api/v1/prompt-packs/{packId}", new
        {
            base_prompt = "Base v2",
            harness_rules = "Harness v2",
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32().ShouldBe(2);

        var versions = await _http.GetFromJsonAsync<JsonElement[]>(
            $"/api/v1/prompt-packs/{packId}/versions");
        versions!.Select(v => v.GetProperty("version").GetInt32()).ShouldBe([1, 2]);

        var compare = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/prompt-packs/{packId}/compare?from=1&to=2");
        compare.GetProperty("added").ToString().ShouldContain("Base v2");
        compare.GetProperty("removed").ToString().ShouldContain("Base v1");

        var rollback = await _http.PostAsync(
            $"/api/v1/prompt-packs/{packId}/rollback/1", null);
        rollback.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rolled = await rollback.Content.ReadFromJsonAsync<JsonElement>();
        rolled.GetProperty("version").GetInt32().ShouldBe(3);
        rolled.GetProperty("base_prompt").GetString()!.ShouldContain("Base v1");

        var active = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/prompt-packs/{packId}");
        active.GetProperty("version").GetInt32().ShouldBe(3);
    }
}