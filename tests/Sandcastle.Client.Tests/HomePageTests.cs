using System.Net;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-05 bUnit tests — componentes-chave renderizam estados
/// (running / confirmation / error) sem depender de backend real.
/// </summary>
public class HomePageTests : BunitContext
{
    public HomePageTests()
    {
        // SPEC-45: Home renderiza ChatComposer (Toasts/JS; Api por teste).
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new ToastService());
        L10nStub.Register(this);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static SandcastleApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new HttpClient(new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void Home_Renders_Brand_And_New_Conversation_Form()
    {
        Services.AddSingleton(Api(_ =>
            Json("""{"items":[],"next_page_id":null}""")));

        var cut = Render<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Sandcastle");
            cut.Markup.ShouldContain("Executar");
        });
    }

    [Fact]
    public void Home_Lists_Conversations_With_Status_Badges()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"items":[{"id":"c1","title":"fix bug","execution_status":"RUNNING",
                       "tags":[],"pinned":false,"confirmation_mode":false,
                       "created_at":"2026-01-01T00:00:00Z",
                       "updated_at":"2026-01-01T00:00:00Z"}],
              "next_page_id":null}
            """)));

        var cut = Render<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("fix bug");
            cut.Markup.ShouldContain("RUNNING");
            cut.Find("a.conv-card").GetAttribute("href")
                .ShouldBe("/conversations/c1");
        });
    }

    [Fact]
    public void Home_Renders_Llm_And_Agent_Profile_Selectors()
    {
        Services.AddSingleton(Api(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/llm-profiles" => Json("""
                [{"id":"11111111-1111-1111-1111-111111111111","scope":"user",
                  "name":"fast coder","provider_connection_id":null,
                  "provider":"openai","model":"gpt-5","temperature":0.2,
                  "role_models":{},"fallback_models":[],
                  "created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z"}]
                """),
            "/api/v1/agent-profiles" => Json("""
                [{"id":"22222222-2222-2222-2222-222222222222","scope":"user",
                  "name":"repo fixer","description":null,"prompt_pack":null,
                  "agent_type":"default","mode":"build","runtime_profile":null,
                  "llm_profile_id":null,"allowed_tools":[],"skills":[],
                  "created_at":"2026-01-01T00:00:00Z",
                  "updated_at":"2026-01-01T00:00:00Z"}]
                """),
            _ => Json("""{"items":[],"next_page_id":null}"""),
        }));

        var cut = Render<Home>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("fast coder");
            cut.Markup.ShouldContain("repo fixer");
        });
    }
}