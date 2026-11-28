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

/// <summary>SPEC-180 RF-003/RF-004: LLM and agent profile CRUD + selection safety.</summary>
public sealed class Spec180ProfileEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-prof-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec180ProfileEndpointsTests()
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
    public async Task LlmProfile_Crud_Validates_Connection_And_Protects_Delete_In_Use()
    {
        (await _http.PostAsJsonAsync("/api/v1/llm-profiles", new
        {
            name = "broken",
            model = "gpt-x",
            provider_connection_id = Guid.NewGuid(),
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var connection = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "openai",
            provider = "openai",
            secret_value = "sk-x",
        });
        var connectionId = (await connection.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var create = await _http.PostAsJsonAsync("/api/v1/llm-profiles", new
        {
            name = "Code primary",
            provider = "openai",
            model = "gpt-5",
            provider_connection_id = connectionId,
            temperature = 0.2,
            role_models = new { code = "gpt-5", review = "gpt-5-mini" },
            fallback_models = new[] { "gpt-5-mini" },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/llm-profiles");
        list!.Single().GetProperty("model").GetString().ShouldBe("gpt-5");

        var agent = await _http.PostAsJsonAsync("/api/v1/agent-profiles", new
        {
            name = "coder",
            llm_profile_id = id,
        });
        agent.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _http.DeleteAsync($"/api/v1/llm-profiles/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AgentProfile_Crud_Duplicate_And_Validation()
    {
        (await _http.PostAsJsonAsync("/api/v1/agent-profiles", new
        {
            name = "bad",
            mode = "yeet",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var create = await _http.PostAsJsonAsync("/api/v1/agent-profiles", new
        {
            name = "Safe reviewer",
            description = "read-only review",
            prompt_pack = "Review carefully.",
            agent_type = "readonly",
            mode = "plan",
            runtime_profile = "small",
            allowed_tools = new[] { "file_read", "git_diff" },
            skills = new[] { "code-review" },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var doc = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetGuid();
        doc.GetProperty("mode").GetString().ShouldBe("plan");

        var duplicate = await _http.PostAsync($"/api/v1/agent-profiles/{id}/duplicate", null);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await duplicate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("name").GetString().ShouldBe("Safe reviewer copy");

        var update = await _http.PutAsJsonAsync($"/api/v1/agent-profiles/{id}", new
        {
            description = "updated",
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.DeleteAsync($"/api/v1/agent-profiles/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Conversation_Create_Materializes_Profile_Defaults_And_Rejects_Invisible_Profile()
    {
        var llm = await _http.PostAsJsonAsync("/api/v1/llm-profiles", new
        {
            name = "cheap",
            model = "gpt-5-mini",
        });
        var llmId = (await llm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var agent = await _http.PostAsJsonAsync("/api/v1/agent-profiles", new
        {
            name = "planner",
            agent_type = "plan",
            mode = "plan",
            runtime_profile = "small",
            llm_profile_id = llmId,
            allowed_tools = new[] { "file_read" },
        });
        var agentId = (await agent.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var create = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new { role = "user", content = new object[] { "hello" }, run = false },
            llm_profile_id = llmId,
            agent_profile_id = agentId,
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conv = (await db.Conversations.ToListAsync()).OrderByDescending(c => c.CreatedAt).First();
        conv.LlmModel.ShouldBe("gpt-5-mini");
        conv.AgentType.ShouldBe("plan");
        conv.Mode.ShouldBe("plan");
        conv.SandboxProfile.ShouldBe("small");
        conv.LlmProfileId.ShouldBe(llmId);
        conv.AgentProfileId.ShouldBe(agentId);
        conv.PermissionsJson!.ShouldContain("file_read");

        var agentOnly = await _http.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new { role = "user", content = new object[] { "agent only" }, run = false },
            agent_profile_id = agentId,
        });
        agentOnly.StatusCode.ShouldBe(HttpStatusCode.Created);
        var agentOnlyConv = (await db.Conversations.ToListAsync())
            .OrderByDescending(c => c.CreatedAt).First();
        agentOnlyConv.AgentProfileId.ShouldBe(agentId);
        agentOnlyConv.LlmProfileId.ShouldBe(llmId);
        agentOnlyConv.LlmModel.ShouldBe("gpt-5-mini");

        (await _http.DeleteAsync($"/api/v1/agent-profiles/{agentId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _http.DeleteAsync($"/api/v1/llm-profiles/{llmId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var other = TestAuth.Login(_factory, "other-profiles@example.com");
        var denied = await other.PostAsJsonAsync("/api/v1/app-conversations", new
        {
            initial_message = new { role = "user", content = new object[] { "hello" }, run = false },
            llm_profile_id = llmId,
        });
        denied.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var automation = await _http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "profile automation",
            prompt = "run with profiles",
            schedule_kind = "interval",
            interval_minutes = 60,
            llm_profile_id = llmId,
            agent_profile_id = agentId,
        });
        automation.StatusCode.ShouldBe(HttpStatusCode.Created);

        var invalidAutomation = await _http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "invalid profile automation",
            prompt = "reject missing profile",
            schedule_kind = "interval",
            interval_minutes = 60,
            llm_profile_id = Guid.NewGuid(),
        });
        invalidAutomation.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        other.Dispose();
    }
}