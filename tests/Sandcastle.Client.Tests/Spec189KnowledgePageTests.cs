using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Pages;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec189KnowledgePageTests : BunitContext
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(fn(r)); }
    private static HttpResponseMessage Json(object v) => new(HttpStatusCode.OK) { Content = JsonContent.Create(v, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };
    public Spec189KnowledgePageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; L10nStub.Register(this);
        var collectionId = Guid.NewGuid();
        var http = new HttpClient(new Handler(req => req.RequestUri!.ToString() switch
        {
            var u when u.Contains("knowledge/collections") && !u.Contains("documents") => Json(new[] { new { id = collectionId, org_id = (Guid?)null, owner_user_id = "u", name = "docs", repo_path = "owner/repo", description = (string?)null, document_count = 1, chunk_count = 2, created_at = DateTimeOffset.Now, updated_at = DateTimeOffset.Now } }),
            var u when u.Contains("knowledge/suggestions") => Json(Array.Empty<object>()),
            var u when u.Contains("documents") => Json(new[] { new { id = Guid.NewGuid(), collection_id = collectionId, title = "runbook", source_uri = "repo://runbook.md", content_hash = "abc", chunk_count = 2, lineage_json = "{}", created_at = DateTimeOffset.Now } }),
            _ => Json(new { })
        }))
        { BaseAddress = new Uri("http://localhost:8080") };
        Services.AddSingleton(new SandcastleApiClient(http));
    }
    [Fact]
    public void Knowledge_Renders_Collections_In_Portuguese()
    {
        var cut = Render<KnowledgePage>();
        cut.WaitForAssertion(() => { cut.Markup.ShouldContain("Coleções"); cut.Markup.ShouldContain("docs"); });
        cut.Find("section.knowledge-v2 select").Change(cut.FindAll("section.knowledge-v2 select option").Last().GetAttribute("value"));
        cut.WaitForAssertion(() => { cut.Markup.ShouldContain("Testar retrieval"); cut.Markup.ShouldContain("runbook"); });
    }
}