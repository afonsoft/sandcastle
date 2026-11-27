using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using Sandcastle.Client.Components.Conversation;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-166 RF-005: painel de insights renderiza L2/action items.</summary>
public sealed class Spec166InsightsTabTests : BunitContext
{
    public Spec166InsightsTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"00000000-0000-0000-0000-000000000001","conversation_id":"00000000-0000-0000-0000-000000000002","org_id":null,"outcome":"success","issues":[],"dead_ends":[],"phase_costs":[],"suggested_prompt":null,"learnings":[],"model":"fake","created_at":"2026-11-27T00:00:00Z","category":"feature","size_tier":"M","action_items":["Add regression coverage"],"llm_summary":"Implemented cleanly.","timeline":{"implementation":"core flow"},"llm_analysis_failed":false}
                    """, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public void PainelInsights_Renderiza_ActionItems_L2()
    {
        var api = new SandcastleApiClient(new HttpClient(new StubHandler())
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        Services.AddSingleton(api);
        var state = new ConversationStateService(api,
            new ConversationHubClient(new Uri("http://localhost/hubs/conversations")));
        typeof(ConversationStateService)
            .GetProperty(nameof(ConversationStateService.Conversation))!
            .SetValue(state, new ConversationItem { Id = "conv-1", ExecutionStatus = "READY" });

        var cut = Render<InsightsTab>(p => p.Add(x => x.State, state));

        cut.WaitForAssertion(() =>
            cut.Markup.ShouldContain("Add regression coverage"));
        cut.Markup.ShouldContain("Implemented cleanly.");
        cut.Markup.ShouldContain("implementation");
    }
}
