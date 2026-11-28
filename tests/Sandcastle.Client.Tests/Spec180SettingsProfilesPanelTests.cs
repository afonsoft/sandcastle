using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Settings;
using Sandcastle.Client.Services;

using Shouldly;

using Xunit;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-180 RF-001/RF-002: painel de profiles no settings center.</summary>
public sealed class Spec180SettingsProfilesPanelTests : BunitContext
{
    private readonly ProfileApiHandler _handler = new();

    public Spec180SettingsProfilesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton(new ToastService());
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));
    }

    [Fact]
    public void Panel_Renders_Profile_Sections_And_Accessible_Forms_In_PtBr()
    {
        var cut = Render<SettingsProfilesPanel>();

        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Conexões de provider"));
        cut.Markup.ShouldContain("Perfis de LLM");
        cut.Markup.ShouldContain("Perfis de agente");
        cut.Find("section[aria-labelledby='provider-connections-title']")
            .ShouldNotBeNull();
        cut.Find("input[aria-label='Nome']").ShouldNotBeNull();
        cut.Find("select[aria-label='Provider']").ShouldNotBeNull();
    }

    [Fact]
    public void Create_Connection_Posts_Secret_Value_And_Reloads_List()
    {
        var cut = Render<SettingsProfilesPanel>();
        var section = cut.Find("section[aria-labelledby='provider-connections-title']");
        section.QuerySelector("input[aria-label='Nome']")!
            .Change("Work OpenAI");
        section.QuerySelector("input[type='password']")!
            .Change("sk-test-secret");

        section.QuerySelector("button.primary")!.Click();

        cut.WaitForAssertion(() =>
            _handler.LastProviderBody.ShouldContain("sk-test-secret"));
        _handler.LastProviderBody.ShouldContain("Work OpenAI");
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Work OpenAI"));
    }

    private sealed class ProfileApiHandler : HttpMessageHandler
    {
        public string LastProviderBody { get; private set; } = "";
        private bool _created;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path == "/api/v1/provider-connections")
            {
                LastProviderBody = await request.Content!
                    .ReadAsStringAsync(cancellationToken);
                _created = true;
                return Json("""
                {"id":"11111111-1111-1111-1111-111111111111","scope":"user","name":"Work OpenAI","provider":"openai","base_url":"https://api.openai.com/v1","secret_ref":"provider_connection_x","enabled":true,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}
                """);
            }

            if (path == "/api/v1/provider-connections" && _created)
            {
                return Json("""
                [{"id":"11111111-1111-1111-1111-111111111111","scope":"user","name":"Work OpenAI","provider":"openai","base_url":"https://api.openai.com/v1","secret_ref":"provider_connection_x","enabled":true,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}]
                """);
            }

            return Json("[]");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "application/json"),
            };
    }
}