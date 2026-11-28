using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-195: project workspace pages render shared context.</summary>
public class Spec195ProjectsPageTests : BunitContext
{
    public Spec195ProjectsPageTests()
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
    public void ProjectsPage_Renders_List_And_Create_Form()
    {
        Services.AddSingleton(Api(_ => Json("""
            [{"id":"11111111-1111-1111-1111-111111111111","name":"Migration program",
              "description":null,"repositories":["owner/repo"],
              "coordinator_conversation_id":"22222222-2222-2222-2222-222222222222",
              "status":"active","created_at":"2026-01-01T00:00:00Z",
              "updated_at":"2026-01-01T00:00:00Z","archived_at":null,"scope":"user"}]
            """)));

        var cut = Render<ProjectsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Projetos");
            cut.Markup.ShouldContain("Migration program");
            cut.Markup.ShouldContain("Vinculado");
            cut.Find("a[href='/projects/11111111-1111-1111-1111-111111111111']");
        });
    }

    [Fact]
    public void ProjectDetailPage_Renders_Coordinator_Workers_Memory_And_Files()
    {
        Services.AddSingleton(Api(_ => Json("""
            {"id":"11111111-1111-1111-1111-111111111111","name":"Migration program",
             "description":"coordinate","repositories":["owner/repo"],
             "coordinator_conversation_id":"22222222-2222-2222-2222-222222222222",
             "status":"active","created_at":"2026-01-01T00:00:00Z",
             "updated_at":"2026-01-01T00:00:00Z","archived_at":null,"scope":"user",
             "workers":[{"conversation_id":"33333333-3333-3333-3333-333333333333",
               "created_at":"2026-01-01T00:00:00Z"}],
             "memories":[{"key":"decision","value":"phased","updated_at":"2026-01-01T00:00:00Z"}],
             "files":[{"path":"docs/plan.md","content_type":"text/markdown","size_bytes":12,
               "updated_at":"2026-01-01T00:00:00Z"}]}
            """)));

        var cut = Render<ProjectDetailPage>(p => p.Add(x => x.Id,
            Guid.Parse("11111111-1111-1111-1111-111111111111")));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Migration program");
            cut.Markup.ShouldContain("Abrir coordenador");
            cut.Markup.ShouldContain("33333333-3333-3333-3333-333333333333");
            cut.Markup.ShouldContain("phased");
            cut.Markup.ShouldContain("docs/plan.md");
        });
    }
}