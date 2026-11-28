using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Settings;
using Sandcastle.Client.Services;

using Shouldly;

using Xunit;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-180 RF-001/RF-005: shell categorizada e seções unificadas.</summary>
public sealed class Spec180SettingsContentTests : BunitContext
{
    public Spec180SettingsContentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var http = new HttpClient(new SettingsApiHandler())
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        var api = new SandcastleApiClient(http);
        var js = JSInterop.JSRuntime;
        var toasts = new ToastService();
        var hub = new ConversationHubClient(
            new Uri("http://localhost:8080/hubs/conversations"));
        var inbox = new AttentionInboxState(api, hub, toasts, js);
        Services.AddSingleton(http);
        Services.AddSingleton(api);
        Services.AddSingleton(toasts);
        Services.AddSingleton(new ThemeService(js, api, http));
        Services.AddSingleton(inbox);
        Services.AddSingleton(new ChatNotificationsService(inbox, js));
        Services.AddSingleton(new AdminState(new AdminApiClient(http)));
        Services.AddSingleton(sp =>
        {
            var nav = sp.GetRequiredService<
                Microsoft.AspNetCore.Components.NavigationManager>();
            var svc = new AuthStateService(new HttpClient(
                new SettingsApiHandler())
            { BaseAddress = new Uri("http://localhost:8080") }, nav);
            svc.InitializeAsync().GetAwaiter().GetResult();
            return svc;
        });
    }

    [Fact]
    public void Settings_Sections_Renders_Categories_And_Unified_Links_In_PtBr()
    {
        var cut = Render<SettingsContent>(p => p.Add(x => x.InitialTab, "sections"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Seções"));
        cut.Markup.ShouldContain("Workspace");
        cut.Markup.ShouldContain("Operações");
        cut.Markup.ShouldContain("Conta");
        cut.Find("a[href='/settings/mcp']").ShouldNotBeNull();
        cut.Find("a[href='/extensions']").ShouldNotBeNull();
        cut.Find("a[href='/settings/skills']").ShouldNotBeNull();
        cut.Find("a[href='/automations']").ShouldNotBeNull();
        cut.FindAll("a[href='/admin/flags']").ShouldBeEmpty();
    }

    [Fact]
    public void Settings_Agent_Tab_Shows_Sticky_SaveBar_For_Dirty_State_And_Discard()
    {
        var cut = Render<SettingsContent>(p => p.Add(x => x.InitialTab, "agent"));

        cut.WaitForAssertion(() =>
            cut.FindAll(".settings-savebar").ShouldBeEmpty());
        cut.Find(".settings-panel .field input").Input("gpt-5-mini");

        cut.WaitForAssertion(() =>
        {
            cut.Find(".settings-savebar").ShouldNotBeNull();
            cut.Markup.ShouldContain("alterações não salvas");
        });
        cut.Find(".settings-savebar-discard").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll(".settings-savebar").ShouldBeEmpty());
    }

    [Fact]
    public void Settings_Profiles_Tab_Renders_Profile_Control_Center()
    {
        var cut = Render<SettingsContent>(p => p.Add(x => x.InitialTab, "profiles"));

        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Conexões de provider"));
        cut.Markup.ShouldContain("Perfis de LLM");
        cut.Markup.ShouldContain("Perfis de agente");
    }


    [Fact]
    public void Settings_Profiles_Tab_Renders_Agent_Definitions_And_Prompt_Packs()
    {
        var cut = Render<SettingsContent>(p => p.Add(x => x.InitialTab, "profiles"));

        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Definições de agentes"));
        cut.Markup.ShouldContain("Prompt packs");
        cut.Markup.ShouldContain("repo-reviewer");
        cut.Markup.ShouldContain("default-cloud");
    }

    private sealed class SettingsApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/v1/admin/super-admins/me" => "{\"is_super_admin\":false}",
                "/themes/builtin.json" => "[]",
                "/api/v1/provider-connections" => "[]",
                "/api/v1/llm-profiles" => "[]",
                "/api/v1/agent-profiles" => "[]",
                "/api/v1/agent-definitions" => """[{"id":"00000000-0000-0000-0000-000000000001","scope":"user","name":"repo-reviewer","description":"reviews","instructions":"review","tools":["file_read"],"model":null,"model_profile":null,"skills":[],"mode":"plan","permission_preset":"read-only","enabled":true,"persisted":true,"source":"user"}]""",
                "/api/v1/prompt-packs" => """[{"id":"00000000-0000-0000-0000-000000000002","pack_id":"00000000-0000-0000-0000-000000000003","scope":"user","name":"default-cloud","base_prompt":"base","harness_rules":null,"plan_mode_prompt":null,"build_mode_prompt":null,"advisor_prompt":null,"permission_judge_prompt":null,"version":1,"active":true,"created_at":"2026-01-01T00:00:00Z"}]""",
                "/api/v1/secrets" => "[]",
                "/api/v1/settings/llm-keys/providers" => "[]",
                "/api/v1/settings/llm-keys" => "[]",
                "/api/v1/outposts" => "[]",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}