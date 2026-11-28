using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Layout;
using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

using Xunit;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-179: Devin-style shell — topbar contextual, command center,
/// rotas de produto e modo foco.
/// </summary>
public class Spec179NavigationShellTests : BunitContext
{
    public Spec179NavigationShellTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var toasts = new ToastService();
        Services.AddSingleton(toasts);
        var api = new SandcastleApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(api);
        var adminApi = new AdminApiClient(new HttpClient
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(adminApi);
        Services.AddSingleton(new ThemeService(JSInterop.JSRuntime, api,
            new HttpClient { BaseAddress = new Uri("http://localhost:8080") }));
        var hub = new ConversationHubClient(
            new Uri("http://localhost:8080/hubs/conversations"));
        Services.AddSingleton(hub);
        var inbox = new AttentionInboxState(api, hub, toasts,
            JSInterop.JSRuntime);
        Services.AddSingleton(inbox);
        Services.AddSingleton(new ChatNotificationsService(inbox,
            JSInterop.JSRuntime));
        Services.AddSingleton(new AdminState(adminApi));
        Services.AddSingleton(new PaletteService());
        Services.AddSingleton(new DialogService());
        AuthStub.Register(this);
    }

    private IRenderedComponent<MainLayout> RenderShell() =>
        Render<MainLayout>(p => p.Add(
            x => x.Body, (RenderFragment)(_ => { })));

    [Fact]
    public void Shell_Renders_Desktop_Topbar_Breadcrumbs_And_Command_Button()
    {
        var cut = RenderShell();

        cut.Find(".desktop-topbar").ShouldNotBeNull();
        cut.Find(".breadcrumbs").ShouldNotBeNull();
        cut.Find("button.cmdk-open").ShouldNotBeNull();
        cut.Find("button.focus-toggle").ShouldNotBeNull();
    }

    [Fact]
    public void Sidebar_Links_Product_Routes_For_Extensions_And_Factories()
    {
        var cut = RenderShell();

        cut.Find("a.sb-item[href='/extensions']").ShouldNotBeNull();
        cut.Find("a.sb-item[href='/factories']").ShouldNotBeNull();
    }

    [Fact]
    public void Focus_Mode_Hides_Sidebar_And_Exposes_Expanded_State()
    {
        var cut = RenderShell();
        var toggle = cut.Find("button.focus-toggle");

        toggle.Click();

        cut.WaitForAssertion(() =>
            cut.Find(".shell").ClassList.ShouldContain("focus-mode"));
        cut.Find("button.focus-toggle").GetAttribute("aria-pressed")
            .ShouldBe("true");
    }

    [Fact]
    public async Task Command_Palette_Includes_Product_Areas_Beyond_Conversations()
    {
        var cut = RenderShell();
        var palette = Services.GetRequiredService<PaletteService>();

        await palette.OpenAsync();

        var labels = palette.Filtered.Select(i => i.Label).ToList();
        labels.ShouldContain("Automações");
        labels.ShouldContain("Conhecimento");
        labels.ShouldContain("Extensões");
        labels.ShouldContain("Factories");
        labels.ShouldContain("Workspace");
        cut.Dispose();
    }

    [Fact]
    public void Foundation_Routes_Render_Product_Entrypoints()
    {
        var extensions = Render<ExtensionsPage>();
        extensions.Find("a.foundation-card[href='/settings/mcp']")
            .ShouldNotBeNull();
        extensions.Find("a.foundation-card[href='/marketplace']")
            .ShouldNotBeNull();

        // SPEC-183 substituiu o placeholder /factories por CRUD real;
        // a cobertura da página real está em Spec183FactoriesPagesTests.
    }

    [Fact]
    public void Sidebar_Renders_Conversation_Status_And_Cost()
    {
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(
            new StaticJsonHandler("""
            {"items":[{"id":"abc-def","title":"paid session","execution_status":"RUNNING","needs_attention":"confirmation","cost_usd":0.01,"created_at":"2026-11-27T00:00:00Z","updated_at":"2026-11-27T00:00:00Z"}],"next_page_id":null}
            """))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));

        var cut = RenderShell();

        cut.WaitForAssertion(() =>
            cut.Find("a.sb-item[href='/conversations/abc-def'] .dot")
                .ClassList.ShouldContain("attention-confirmation"));
        cut.Find(".conv-cost").TextContent.Trim().ShouldBe("$0.01");
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

}