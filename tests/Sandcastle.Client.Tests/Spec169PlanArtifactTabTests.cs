using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Conversation;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-169 RF-005: plan approval panel rendering and decisions.</summary>
public sealed class Spec169PlanArtifactTabTests : BunitContext
{
    public Spec169PlanArtifactTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            if (request.Method == HttpMethod.Get && request.RequestUri!.ToString().EndsWith("/plan"))
            {
                return Json(new
                {
                    id = Guid.NewGuid(),
                    conversation_id = Guid.NewGuid(),
                    version = 1,
                    executed_at = (string?)null,
                    items = new[]
                    {
                        new
                        {
                            id = Guid.NewGuid(), title = "edit", files = new[] { "a.cs" },
                            rationale = "why", status = "pending", comments = Array.Empty<string>(),
                        },
                    },
                });
            }
            return Json(new { });
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }),
        };
    }

    [Fact]
    public void Painel_Renderiza_Item_E_Envia_Aprovacao()
    {
        var handler = new StubHandler();
        Services.AddSingleton(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var id = Guid.NewGuid().ToString();
        var cut = Render<PlanArtifactTab>(p => p.Add(x => x.ConversationId, id));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("edit");
            cut.Markup.ShouldContain("Aprovação do plano");
        });
        cut.Find("input").Change("looks good");
        cut.FindAll("button").First(b => b.TextContent.Contains("Aprovar")).Click();

        cut.WaitForAssertion(() =>
            handler.Requests.ShouldContain(r => r.Method == "POST"
                && r.Url.Contains("/decision") && r.Body.Contains("approve")
                && r.Body.Contains("looks good")));
    }
}