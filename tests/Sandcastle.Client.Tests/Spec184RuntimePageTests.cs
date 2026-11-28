using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages.Admin;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec184RuntimePageTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(fn(request));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(value, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };

    public Spec184RuntimePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        L10nStub.Register(this);
        var http = new HttpClient(new Handler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("super-admins/me")) return Json(new { is_super_admin = true });
            if (url.Contains("runtime/fleet")) return Json(new
            {
                capacity = new { total_sandboxes = 2, running = 1, paused = 0, error = 0, warm_available = 1, reserved_memory_mb = 512, reserved_cpu = 1.0, queue_depth = 0, provisioning_latency_seconds = 3.0, failure_rate = 0.0 },
                items = new[] { new { sandbox_id = Guid.NewGuid(), conversation_id = (Guid?)null, status = "running", pool = "cold", image = "agent:latest", profile = "small", memory_mb = 512, cpu_limit = 1.0, node = "local", owner_user_id = "u", health = "healthy", created_at = DateTimeOffset.UtcNow, age = TimeSpan.FromMinutes(2), last_error = (string?)null } },
                alerts = Array.Empty<string>(),
            });
            if (url.Contains("runtime/pools")) return Json(new[] { new { image = "agent:latest", desired = 1, available = 1, claimed = 0, stale = 0, health = "healthy" } });
            return Json(new { });
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        var api = new AdminApiClient(http);
        Services.AddSingleton(api);
        Services.AddSingleton(new AdminState(api));
    }

    [Fact]
    public void Runtime_Page_Renders_Fleet_Pools_And_Capacity_In_Portuguese()
    {
        var cut = Render<AdminRuntimePage>();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Control plane de runtimes");
            cut.Markup.ShouldContain("agent:latest");
            cut.Markup.ShouldContain("Warm pools");
        });
    }
}