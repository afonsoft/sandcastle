using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec186CollaborationPagesTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(fn(r)); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(value, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };

    public Spec186CollaborationPagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var http = new HttpClient(new Handler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/api/v1/channels")) return Json(new[] { new { id = Guid.NewGuid(), org_id = (Guid?)null, owner_user_id = "u", name = "geral", type = "group", pinned = false, unread_count = 0, created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now } });
            if (url.Contains("/messages")) return Json(Array.Empty<object>());
            if (url.EndsWith("/api/v1/notes")) return Json(new[] { new { id = Guid.NewGuid(), org_id = (Guid?)null, owner_user_id = "u", title = "runbook", body = "texto", version = 1, conversation_id = (Guid?)null, knowledge_link = (string?)null, created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now } });
            if (url.EndsWith("/api/v1/notifications")) return Json(new[] { new { id = Guid.NewGuid(), org_id = (Guid?)null, user_id = "u", type = "mention", title = "menção", body = "body", deep_link = "/", read = false, created_at = DateTimeOffset.Now } });
            if (url.Contains("/calendar/events")) return Json(new[] { new { id = "a", source = "automation", title = "nightly", status = "scheduled", repository = "repo", user_id = "u", start_at = DateTimeOffset.Now, end_at = (DateTimeOffset?)null, deep_link = "/" } });
            return Json(new { });
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        Services.AddSingleton(new SandcastleApiClient(http));
    }

    [Fact]
    public void Channels_And_Notes_Render_In_Portuguese()
    {
        var channels = Render<ChannelsPage>(); channels.WaitForAssertion(() => channels.Markup.ShouldContain("Canais"));
        var notes = Render<NotesPage>(); notes.WaitForAssertion(() => notes.Markup.ShouldContain("runbook"));
    }

    [Fact]
    public void Calendar_And_Notifications_Render_In_Portuguese()
    {
        var calendar = Render<CalendarPage>(); calendar.WaitForAssertion(() => calendar.Markup.ShouldContain("nightly"));
        var feed = Render<NotificationsPage>(); feed.WaitForAssertion(() => feed.Markup.ShouldContain("menção"));
    }
}