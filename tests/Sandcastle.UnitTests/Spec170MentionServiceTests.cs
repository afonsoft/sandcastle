using System.Net;
using System.Text;

using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.UnitTests;

/// <summary>SPEC-170 RF-001/004: mention ranking, parser tolerance and cache.</summary>
public sealed class Spec170MentionServiceTests
{
    private sealed class CountingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(fn(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public void MentionFormat_Parses_Structured_And_Ignores_Malformed()
    {
        var parsed = MentionFormat.Parse(
            "ask @[Release audit](conversation:abc) and @[broken](x) now");
        parsed.Count.ShouldBe(1);
        parsed[0].Label.ShouldBe("Release audit");
        parsed[0].Type.ShouldBe("conversation");
        MentionFormat.DisplayText(
            "ask @[Release audit](conversation:abc) now")
            .ShouldBe("ask Release audit now");
    }

    [Fact]
    public void Rank_Prefix_Beats_Substring()
    {
        var ranked = MentionService.Rank([
            new MentionService.MentionItem("skill", "1", "my fix"),
            new MentionService.MentionItem("skill", "2", "fix audit"),
        ], "fix");
        ranked[0].Id.ShouldBe("2");
    }

    [Fact]
    public async Task SearchServer_Caches_Repeated_Query()
    {
        var handler = new CountingHandler(_ => Json("""
            {"items":[{"type":"skill","id":"s1","label":"fix audit",
                       "snippet":"review helper","icon":"sparkles"}]}
            """));
        var svc = new MentionService(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        });
        var first = await svc.SearchServerAsync("fix");
        var second = await svc.SearchServerAsync("fix");
        first[0].Label.ShouldBe("fix audit");
        second[0].Type.ShouldBe("skill");
        handler.Calls.ShouldBe(1);
    }
}