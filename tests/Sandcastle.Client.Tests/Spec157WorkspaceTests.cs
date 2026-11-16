using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-157 — /workspace: tabs por querystring, tabela de itens por
/// kind com badge de escopo, e tab de tools MCP read-only.
/// </summary>
public class Spec157WorkspaceTests : BunitContext
{
    public Spec157WorkspaceTests()
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
    public void Workspace_PromptsTab_Lists_Items_With_Scope()
    {
        Services.AddSingleton(Api(r =>
            r.RequestUri!.Query.Contains("kind=prompt")
                ? Json("""
                      [{"id":"00000000-0000-0000-0000-000000000001","kind":"prompt",
                        "name":"My prompt","scope":"personal",
                        "updated_at":"2026-01-01T00:00:00Z","body":{"text":"hi"}},
                       {"id":"00000000-0000-0000-0000-000000000002","kind":"prompt",
                        "name":"Org prompt","scope":"org",
                        "updated_at":"2026-01-01T00:00:00Z","body":{"text":"hi"}}]
                      """)
                : Json("[]")));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/workspace");
        var cut = Render<WorkspacePage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("My prompt");
            cut.Markup.ShouldContain("Org prompt");
            cut.Markup.ShouldContain("badge-dim");
        });
    }

    [Fact]
    public void Workspace_PresetsTab_Uses_ModelPresetKind()
    {
        var hits = new List<string>();
        Services.AddSingleton(Api(r =>
        {
            hits.Add(r.RequestUri!.Query);
            return Json("[]");
        }));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/workspace?tab=presets");
        var cut = Render<WorkspacePage>();
        cut.WaitForAssertion(() =>
            hits.ShouldContain(q => q.Contains("kind=model_preset")));
    }

    [Fact]
    public void Workspace_ToolsTab_Renders_Catalog()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"servers":[],"tools":[{"server":"fs","name":"read_file",
              "full_name":"fs.read_file","description":"reads"}]}
            """)));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/workspace?tab=tools");
        var cut = Render<WorkspacePage>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("fs"));
    }

    [Fact]
    public void WorkspaceEditor_NewPrompt_Renders_Form()
    {
        Services.AddSingleton(Api(_ => Json("[]")));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/workspace/prompts/new");
        var cut = Render<WorkspaceItemPage>(p => p
            .Add(x => x.Kind, "prompts")
            .Add(x => x.Id, "new"));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("textarea"));
    }
}