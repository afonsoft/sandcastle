using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Sandcastle.Client.Pages.Admin;
using Sandcastle.Client.Services;
using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec185ObservabilityPagesTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(fn(request));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(value, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };

    public Spec185ObservabilityPagesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var http = new HttpClient(new Handler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("super-admins/me")) return Json(new { is_super_admin = true });
            if (url.Contains("analytics/summary")) return Json(new
            {
                series = new[] { new { date = "2026-11-28", tokens = 15, cost_usd = .01, runs = 1, errors = 0 } },
                models = new[] { new { model = "gpt-5", provider = "unknown", tokens = 15, cost_usd = .01, runs = 1, average_latency_ms = 120, errors = 0 } },
                total_tokens = 15, total_cost_usd = .01, total_runs = 1, total_errors = 0,
            });
            if (url.Contains("analytics/rankings")) return Json(new[] { new { id = "c", label = "run", tokens = 15, cost_usd = .01, runs = 1, errors = 0 } });
            if (url.Contains("admin/budgets")) return Json(new[] { new { org_id = Guid.NewGuid(), org_name = "Org", plan_name = "pro", tokens_used = 90, token_limit = 100, cost_usd = 2, used_percent = .9, level = "warning", enforcement_enabled = true, alert = "token budget 90%" } });
            return Json(new { });
        })) { BaseAddress = new Uri("http://localhost:8080") };
        var api = new AdminApiClient(http);
        Services.AddSingleton(api);
        Services.AddSingleton(new AdminState(api));
    }

    [Fact]
    public void Analytics_Renders_Summary_Models_And_Trace_In_Portuguese()
    {
        var cut = Render<AdminAnalyticsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Analytics cloud");
            cut.Markup.ShouldContain("gpt-5");
            cut.Markup.ShouldContain("Waterfall de trace");
        });
    }

    [Fact]
    public void Budgets_Renders_Levels_And_Alerts_In_Portuguese()
    {
        var cut = Render<AdminBudgetsPage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Budgets operacionais");
            cut.Markup.ShouldContain("warning");
            cut.Markup.ShouldContain("token budget 90%");
        });
    }
}
