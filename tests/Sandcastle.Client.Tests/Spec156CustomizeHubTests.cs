using System.Net;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Customize;
using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-156 — hub /customize: tabs por querystring, redirects das rotas
/// legadas e badges de escopo/origem nas listas.
/// </summary>
public class Spec156CustomizeHubTests : BunitContext
{
    public Spec156CustomizeHubTests()
    {
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

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new StubHandler(fn)) { BaseAddress = new Uri("http://localhost:8080") };

    private static SandcastleApiClient Api(
        Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(Http(fn));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public void LegacySkillsRoute_Redirects_To_CustomizeTab()
    {
        var cut = Render<SkillsSettingsPage>();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.ShouldEndWith("/customize?tab=skills");
    }

    [Fact]
    public void LegacyMcpRoute_Redirects_To_CustomizeTab()
    {
        var cut = Render<McpSettingsPage>();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.ShouldEndWith("/customize?tab=mcps");
    }

    [Fact]
    public void Customize_DefaultTab_Is_Knowledge()
    {
        Services.AddSingleton(Api(_ => Json("[]")));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/customize");
        var cut = Render<CustomizePage>();
        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("/knowledge"));
    }

    [Fact]
    public void Customize_SkillsTab_Shows_Scope_And_Source_Badges()
    {
        Services.AddSingleton(Http(_ => Json("""
            [{"id":"00000000-0000-0000-0000-000000000001","name":"mine",
              "description":"d","source":"user","enabled":true},
             {"id":"00000000-0000-0000-0000-000000000002","name":"orgs",
              "description":"d","source":"org","enabled":true}]
            """)));
        Services.AddSingleton(Api(_ => Json("[]")));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/customize?tab=skills");

        var cut = Render<CustomizePage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("mine");
            cut.Markup.ShouldContain("badge");
        });
    }

    [Fact]
    public void KnowledgePanel_Summarizes_And_Links()
    {
        Services.AddSingleton(Api(_ => Json("""
            [{"id":"00000000-0000-0000-0000-000000000001","scope":"repo",
              "repo_path":"a.md","title":"T1","content":"c","kind":"rule",
              "status":"pending","reason":null,"resolution":null,
              "created_at":"2026-01-01T00:00:00Z"}]
            """)));

        var cut = Render<KnowledgePanel>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("T1");
            cut.Markup.ShouldContain("/knowledge");
        });
    }
}