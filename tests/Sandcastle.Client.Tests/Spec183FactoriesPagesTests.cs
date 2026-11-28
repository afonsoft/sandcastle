using System.Net;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-183 RF-005: factories/templates/events UI em pt-BR.</summary>
public sealed class Spec183FactoriesPagesTests : BunitContext
{
    public Spec183FactoriesPagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton(new ToastService());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private void Api(Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(new StubHandler(fn))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));

    private const string FactoryJson = """
        {"id":"11111111-1111-1111-1111-111111111111","owner_user_id":"u",
         "name":"Delivery","description":"factory","automation_count":2,
         "workflow_count":1,"created_at":"2026-11-28T00:00:00Z",
         "updated_at":"2026-11-28T00:00:00Z","org_id":null}
        """;

    [Fact]
    public void Factories_Renders_Factory_And_Workflow_Editor_In_Portuguese()
    {
        Api(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/factories" => Json($"[{FactoryJson}]"),
            "/api/v1/workflows" => Json("[]"),
            _ => Json("[]"),
        });
        var cut = Render<FactoriesPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Delivery");
            cut.Markup.ShouldContain("Entrega autônoma");
            cut.Markup.ShouldContain("Criar workflow");
        });
    }

    [Fact]
    public void Templates_Renders_Recommended_Templates()
    {
        Api(_ => Json("""
            [{"id":"autonomous-delivery","name":"Autonomous delivery",
             "description":"hard gates","kind":"task","prompt":"prompt",
             "schedule_kind":"once","interval_minutes":null,
             "cron_expression":null,"approval_preset":"plan"}]
            """));
        var cut = Render<AutomationTemplatesPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Templates de automação");
            cut.Markup.ShouldContain("Autonomous delivery");
        });
    }

    [Fact]
    public void Events_Renders_Filters_And_Deep_Link()
    {
        Api(req => req.RequestUri!.AbsolutePath == "/api/v1/factories"
            ? Json($"[{FactoryJson}]")
            : Json("""
                [{"id":"workflow:1","source":"workflow",
                 "source_id":"11111111-1111-1111-1111-111111111111",
                 "run_id":"22222222-2222-2222-2222-222222222222",
                 "factory_id":"11111111-1111-1111-1111-111111111111",
                 "factory_name":"Delivery","name":"Ship","status":"waiting_approval",
                 "repository":"owner/repo","conversation_id":null,
                 "occurred_at":"2026-11-28T00:00:00Z",
                 "deep_link":"/workflows/11111111-1111-1111-1111-111111111111",
                 "error":null}]
                """));
        var cut = Render<AutomationEventsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Eventos de automação");
            cut.Markup.ShouldContain("waiting_approval");
            cut.Markup.ShouldContain("/workflows/11111111-1111-1111-1111-111111111111");
        });
    }
}