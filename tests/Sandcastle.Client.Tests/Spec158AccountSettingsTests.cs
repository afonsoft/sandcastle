using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Settings;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-158 — settings account/tokens/notifications/maintenance panels.
/// </summary>
public class Spec158AccountSettingsTests : BunitContext
{
    public Spec158AccountSettingsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new ToastService());
        L10nStub.Register(this);
        // AuthStateService: provider=local → seção de senha visível.
        Services.AddSingleton(sp =>
        {
            var nav = sp.GetRequiredService<
                Microsoft.AspNetCore.Components.NavigationManager>();
            var svc = new AuthStateService(new HttpClient(
                new StubHandler(_ => Json("""
                    {"provider":"local",
                     "user":{"email":"a@x.test",
                             "display_name":"A",
                             "is_super_admin":false}}
                    """)))
            { BaseAddress = new Uri("http://localhost:8080") },
                nav);
            svc.InitializeAsync().GetAwaiter().GetResult();
            return svc;
        });
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static SandcastleApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new HttpClient(new StubHandler(fn))
        { BaseAddress = new Uri("http://localhost:8080") });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    [Fact]
    public void TokensPanel_Lists_Masked_Pats()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"tokens":[{"id":"00000000-0000-0000-0000-000000000001",
              "name":"ci","prefix":"sc_pat_ab12","scopes":["read"],
              "created_at":"2026-01-01T00:00:00Z","last_used_at":null,
              "expires_at":null}]}
            """)));
        var cut = Render<TokensPanel>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("sc_pat_ab12");
            cut.Markup.ShouldContain("ci");
            cut.Markup.ShouldNotContain("sc_pat_ab12deadbeef");
        });
    }

    [Fact]
    public void NotificationsPanel_Loads_Prefs()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"run_finished":true,"run_failed":false,
             "webhook_url":"https://hooks.test/x"}
            """)));
        var cut = Render<NotificationsPanel>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("https://hooks.test/x");
            cut.Find("#ntf-finished")
                .GetAttribute("checked").ShouldNotBeNull();
        });
    }

    [Fact]
    public void MaintenancePanel_Confirm_Gates_Clear()
    {
        Services.AddSingleton(Api(_ => Json("{}")));
        var cut = Render<MaintenancePanel>();
        cut.Markup.ShouldNotContain("Confirmar — limpar agora");
        cut.Find("button.badge-dim").Click();
        cut.Markup.ShouldContain("Confirmar — limpar agora");
    }

    [Fact]
    public void AccountPanel_Shows_Current_Session()
    {
        Services.AddSingleton(Api(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/sessions")
                ? Json("""
                    {"sessions":[{"id":"current","current":true,
                      "auth_type":"cookie","ip":"127.0.0.1",
                      "user_agent":"xunit"}]}
                    """)
                : Json("{}")));
        var cut = Render<AccountPanel>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("current");
            cut.Markup.ShouldContain("127.0.0.1");
        });
    }
}