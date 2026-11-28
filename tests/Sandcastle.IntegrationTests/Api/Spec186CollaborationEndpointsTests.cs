using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

public sealed class Spec186CollaborationEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sc-collab-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;

    public Spec186CollaborationEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
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
    public async Task Channels_Notes_Notifications_And_Calendar_Are_Available()
    {
        var created = await _http.PostAsJsonAsync("/api/v1/channels", new { name = "general", type = "group", org_id = (Guid?)null });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var channel = await created.Content.ReadFromJsonAsync<JsonElement>();
        var channelId = channel.GetProperty("id").GetGuid();
        var message = await _http.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages", new { body = "hello", parent_id = (Guid?)null, conversation_id = (Guid?)null });
        message.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _http.GetAsync("/api/v1/channels")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var messages = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/channels/{channelId}/messages");
        messages[0].GetProperty("body").GetString().ShouldBe("hello");
        var note = await _http.PostAsJsonAsync("/api/v1/notes", new { title = "runbook", body = "v1", org_id = (Guid?)null, conversation_id = (Guid?)null, knowledge_link = (string?)null });
        note.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _http.GetAsync("/api/v1/notifications")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _http.GetAsync("/api/v1/calendar/events")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}