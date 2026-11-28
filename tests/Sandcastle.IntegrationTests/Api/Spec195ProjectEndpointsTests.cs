using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-195: project coordinator workspace endpoints.</summary>
public sealed class Spec195ProjectEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-proj-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec195ProjectEndpointsTests()
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
    public async Task Project_Crud_Memory_Files_And_Worker_Attach_Are_Scoped()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Migration program",
            description = "coordinate migration",
            repositories = new[] { "owner/repo", "owner/docs" },
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var project = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = project.GetProperty("id").GetGuid();
        project.GetProperty("repositories").GetArrayLength().ShouldBe(2);

        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/projects");
        list!.ShouldContain(p => p.GetProperty("id").GetGuid() == id);

        (await _http.PutAsJsonAsync($"/api/v1/projects/{id}/memory/decision", new
        {
            value = "use phased migration",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.PutAsJsonAsync($"/api/v1/projects/{id}/files/docs/plan.md", new
        {
            content_type = "text/markdown",
            text_content = "# shared plan",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var session = await _http.GetFromJsonAsync<JsonElement>("/api/v1/auth/session");
        var userId = session.GetProperty("user").GetProperty("id").GetGuid().ToString();
        var conversationId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Conversations.Add(new ConversationRecord
            {
                Id = conversationId,
                Title = "worker",
                ExecutionStatus = "Ready",
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        (await _http.PostAsJsonAsync($"/api/v1/projects/{id}/workers", new
        {
            conversation_id = conversationId,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{id}");
        detail.GetProperty("workers").EnumerateArray()
            .ShouldContain(w => w.GetProperty("conversation_id").GetGuid() == conversationId);
        detail.GetProperty("memories").EnumerateArray()
            .ShouldContain(m => m.GetProperty("key").GetString() == "decision");
        detail.GetProperty("files").EnumerateArray()
            .ShouldContain(f => f.GetProperty("path").GetString() == "docs/plan.md");

        (await _http.DeleteAsync($"/api/v1/projects/{id}/workers/{conversationId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _http.PostAsync($"/api/v1/projects/{id}/archive", null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/projects"))!
            .ShouldNotContain(p => p.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Project_Rejects_Invalid_Input_And_Oversize_Shared_Content()
    {
        (await _http.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "bad",
            repositories = Array.Empty<string>(),
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var create = await _http.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "limits",
            repositories = new[] { "owner/repo" },
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await _http.PutAsJsonAsync($"/api/v1/projects/{id}/memory/big", new
        {
            value = new string('x', 9 * 1024),
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _http.PutAsJsonAsync($"/api/v1/projects/{id}/files/foo..bar", new
        {
            text_content = "x",
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}