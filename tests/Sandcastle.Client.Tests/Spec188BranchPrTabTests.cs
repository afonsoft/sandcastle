using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Conversation;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec188BranchPrTabTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(fn(r)); }
    private static HttpResponseMessage Json(object v) => new(HttpStatusCode.OK) { Content = JsonContent.Create(v, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    public Spec188BranchPrTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; L10nStub.Register(this); Services.AddSingleton(new ToastService());
        var id = Guid.NewGuid();
        var http = new HttpClient(new Handler(req => req.RequestUri!.ToString() switch
        {
            var u when u.Contains("/branches") => Json(new[] { new { id, title = "main", parent_conversation_id = (Guid?)null, status = "Ready", created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now, pull_request_url = "https://example.test/pr/1", current = true } }),
            var u when u.Contains("/pr-status") => Json(new { repository = "repo", pull_request_url = "https://example.test/pr/1", pull_request_number = 1, pull_request_state = "open", checks = new object[] { }, review_comments = new object[] { }, preview_links = new string[] { }, updated_at = DateTimeOffset.Now }),
            _ => Json(new { })
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        Services.AddSingleton(new SandcastleApiClient(http));
    }
    [Fact]
    public void BranchPrTab_Renders_In_Portuguese()
    {
        var cut = Render<BranchPrTab>(p => p.Add(x => x.ConversationId, Guid.NewGuid().ToString()));
        cut.WaitForAssertion(() => { cut.Markup.ShouldContain("Branches"); cut.Markup.ShouldContain("PR / CI"); cut.Markup.ShouldContain("main"); });
    }
}