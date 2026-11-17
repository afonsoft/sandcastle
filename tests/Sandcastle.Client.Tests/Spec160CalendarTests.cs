using System.Net;
using System.Text;

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-160 RF-004: calendário mensal de automations — grade 7×N,
/// chips por status + next_runs e painel lateral do dia com link
/// para a conversa do run.
/// </summary>
public class Spec160CalendarTests : BunitContext
{
    public Spec160CalendarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        Services.AddSingleton(new ToastService());
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    /// <summary>Fixture: 1 run failed + 1 next_run, ambos hoje.</summary>
    private void RegisterApi(string scheduleJson)
    {
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(
            new StubHandler(req =>
                req.RequestUri!.AbsolutePath
                    == "/api/v1/automations/schedule"
                    ? Json(scheduleJson)
                    : Json("[]")))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));
    }

    private static string ScheduleJson()
    {
        var now = DateTime.UtcNow;
        static string iso(DateTime d) => d.ToString("O");
        return $$"""
        [{
          "automation_id": "{{Guid.NewGuid()}}",
          "name": "nightly",
          "next_runs": ["{{iso(now.AddHours(3))}}"],
          "runs": [{
            "id": "{{Guid.NewGuid()}}",
            "status": "failed",
            "started_at": "{{iso(now.AddHours(-2))}}",
            "duration_ms": 1200,
            "conversation_id": "{{Guid.NewGuid()}}"
          }]
        }]
        """;
    }

    [Fact]
    public void Grid_Renderiza_Chips_Por_Status_E_NextRuns()
    {
        RegisterApi(ScheduleJson());
        var cut = Render<AutomationCalendar>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".cal-day").Count.ShouldBeGreaterThanOrEqualTo(28));
        cut.Find(".cal-chip.cal-failed").TextContent.Trim()
            .ShouldBe("nightly");
        cut.Find(".cal-chip.cal-next").TextContent.Trim()
            .ShouldBe("nightly");
    }

    [Fact]
    public void Click_Day_Mostra_Run_Com_Link_Para_Conversa()
    {
        RegisterApi(ScheduleJson());
        var cut = Render<AutomationCalendar>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".cal-chip").Count.ShouldBeGreaterThan(0));

        cut.FindAll(".cal-day")
            .First(d => d.QuerySelector(".cal-chip") is not null)
            .Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".cal-side a[href^='/conversations/']")
                .ShouldNotBeNull();
            cut.Find(".cal-side .badge.error").TextContent.Trim()
                .ShouldBe("failed");
        });
    }

    [Fact]
    public void Nav_Prev_Muda_Titulo_E_Refaz_Fetch()
    {
        var calls = 0;
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(
            new StubHandler(_ => { calls++; return Json("[]"); }))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));
        var cut = Render<AutomationCalendar>();
        cut.WaitForAssertion(() => calls.ShouldBe(1));

        var title = cut.Find(".cal-head h2").TextContent;
        cut.Find("button[aria-label='Mês anterior']").Click();
        cut.WaitForAssertion(() =>
        {
            calls.ShouldBe(2);
            cut.Find(".cal-head h2").TextContent.ShouldNotBe(title);
        });
    }
}