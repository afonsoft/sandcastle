using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Customize;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-168 RF-004: editor de allowed-tools no painel de skills.</summary>
public sealed class Spec168SkillsPanelTests : BunitContext
{
    public Spec168SkillsPanelTests()
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
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method.Method, url, body));
            if (url.EndsWith("/api/v1/skills", StringComparison.Ordinal)
                && request.Method == HttpMethod.Get)
            {
                return Json(new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), name = "docs-only",
                        description = "docs", source = "user", enabled = true,
                        allowed_tools = new[] { "file_read" },
                    },
                });
            }
            if (url.EndsWith("/api/v1/skills/tool-catalog", StringComparison.Ordinal))
            {
                return Json(new[] { "bash", "file_read" });
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }

        private static HttpResponseMessage Json(object payload) => new(
            HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload,
                options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }),
        };
    }

    [Fact]
    public void Painel_Exibe_Autocomplete_E_Salva_AllowedTools()
    {
        var handler = new StubHandler();
        Services.AddSingleton(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });

        var cut = Render<SkillsPanel>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[list='skill-tool-names']");
            input.GetAttribute("aria-label").ShouldBe("Tools permitidas");
            input.GetAttribute("value").ShouldBe("file_read");
        });
        cut.FindAll("datalist#skill-tool-names option")
            .Select(x => x.GetAttribute("value"))
            .ShouldContain("bash");

        var edit = cut.Find("input[list='skill-tool-names']");
        edit.Change("file_read, bash");

        cut.WaitForAssertion(() =>
        {
            handler.Requests.ShouldContain(r =>
                r.Method == "PATCH" && r.Url.Contains("/api/v1/skills/")
                && r.Body.Contains("replace_allowed_tools")
                && r.Body.Contains("bash"));
        });
    }
}