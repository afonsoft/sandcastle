using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec187ExtensionsPageTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(fn(r)); }
    private static HttpResponseMessage Json(object v) => new(HttpStatusCode.OK) { Content = JsonContent.Create(v, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    public Spec187ExtensionsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; L10nStub.Register(this);
        var http = new HttpClient(new Handler(req => req.RequestUri!.ToString() switch
        {
            var u when u.Contains("extensions/summary") => Json(new { mcp_servers = 2, marketplace_installed = 3, plugins = 1, enabled_plugins = 1, scm_connections = 1, ticket_connections = 1 }),
            var u when u.EndsWith("/api/v1/plugins") => Json(new[] { new { id = Guid.NewGuid(), org_id = (Guid?)null, owner_user_id = "u", name = "hello", version = "1.0.0", manifest_json = "{}", permissions = new[] { "read_workspace" }, enabled = true, rollback_version = (string?)null, created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now } }),
            _ => Json(new { })
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        Services.AddSingleton(new SandcastleApiClient(http));
    }
    [Fact]
    public void Extensions_Renders_Hub_And_Plugins_In_Portuguese()
    {
        var cut = Render<ExtensionsPage>();
        cut.WaitForAssertion(() => { cut.Markup.ShouldContain("Hub unificado"); cut.Markup.ShouldContain("hello"); cut.Markup.ShouldContain("Integrações"); });
    }
}