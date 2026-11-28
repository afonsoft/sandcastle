using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages.Admin;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec190LifecyclePageTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(fn(r)); }
    private static HttpResponseMessage Json(object v) => new(HttpStatusCode.OK) { Content = JsonContent.Create(v, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    public Spec190LifecyclePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; L10nStub.Register(this);
        var http = new HttpClient(new Handler(req => req.RequestUri!.ToString() switch
        {
            var u when u.Contains("super-admins/me") => Json(new { is_super_admin = true }),
            var u when u.Contains("lifecycle/provisioning") => Json(new[] { new { id = Guid.NewGuid(), org_id = (Guid?)null, email = "user@example.com", status = "waitlist", quotas_json = "{}", feature_flags_json = "{}", invite_count = 1, created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now, approved_at = (DateTimeOffset?)null, onboarded_at = (DateTimeOffset?)null } }),
            var u when u.Contains("lifecycle/outbox") => Json(Array.Empty<object>()),
            var u when u.Contains("lifecycle/jobs") => Json(new[] { new { id = Guid.NewGuid(), job_key = "integration-resync", status = "completed", details_json = "{}", created_at = DateTimeOffset.Now, completed_at = DateTimeOffset.Now } }),
            _ => Json(new { })
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        var api = new AdminApiClient(http); Services.AddSingleton(api); Services.AddSingleton(new AdminState(api));
    }
    [Fact]
    public void Lifecycle_Page_Renders_In_Portuguese()
    {
        var cut = Render<AdminLifecyclePage>();
        cut.WaitForAssertion(() => { cut.Markup.ShouldContain("Lifecycle enterprise"); cut.Markup.ShouldContain("user@example.com"); cut.Markup.ShouldContain("integration-resync"); });
    }
}