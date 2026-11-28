using System.Net;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components;
using Sandcastle.Client.Components.Conversation;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>SPEC-170 RF-001/002/003/004: rich mention popup + chips.</summary>
public sealed class Spec170MentionAutocompleteTests : BunitContext
{
    public Spec170MentionAutocompleteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new MarkdownService());
        L10nStub.Register(this);
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

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> fn) =>
        new(new StubHandler(fn)) { BaseAddress = new Uri("http://localhost:8080") };

    private static ClientEvent Ev(string json) => new()
    {
        Raw = JsonDocument.Parse(json).RootElement.Clone(),
    };

    [Fact]
    public void Mention_Shows_Rich_Server_Item_And_Inserts_Structured_Token()
    {
        var http = Http(req => req.RequestUri!.AbsolutePath.EndsWith("/exec")
            ? Json("""{"exit_code":0,"stdout":"","stderr":"","timed_out":false}""")
            : req.RequestUri!.AbsolutePath.EndsWith("/mentions")
                ? Json("""{"items":[{"type":"skill","id":"skill-1","label":"release fixer","snippet":"fix release regressions","icon":"sparkles"}]}""")
                : Json("[]"));
        Services.AddSingleton(new SandcastleApiClient(http));
        Services.AddSingleton(new MentionService(http));
        Services.AddSingleton(new ToastService());
        string? sent = null;
        var cut = Render<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, (string t) => sent = t));

        cut.Find("textarea").Input("please @release");
        cut.WaitForAssertion(() =>
        {
            cut.Find(".mention-item").TextContent.ShouldContain("release fixer");
            cut.Find(".mention-type").TextContent.ShouldContain("skill");
            cut.Find(".mention-snippet").TextContent
                .ShouldContain("fix release regressions");
        }, TimeSpan.FromSeconds(3));

        cut.Find(".mention-item").Click();
        var expected = "@[release fixer](skill:skill-1)";
        cut.WaitForAssertion(() =>
        {
            cut.Find("textarea").GetAttribute("value")!.ShouldContain(expected);
            cut.Find(".chip.mention").TextContent.ShouldContain("@release fixer");
        }, TimeSpan.FromSeconds(2));
        cut.Find(".send-btn").Click();
        sent!.ShouldContain(expected);
    }

    [Fact]
    public void User_Message_Renders_Mention_Chip_And_Display_Label()
    {
        var e = Ev("""
            {"id":"m1","kind":"MessageEvent","source":"user",
             "timestamp":"2026-01-01T00:00:00Z",
             "llm_message":{"role":"user","content":[{"type":"text",
               "text":"check @[Release audit](conversation:abc-def) please"}]}}
            """);
        var cut = Render<EventCard>(p => p.Add(x => x.Event, e));
        var chip = cut.Find(".chip.mention-conversation");
        chip.TextContent.ShouldContain("@Release audit");
        chip.GetAttribute("href")!.ShouldBe("/conversations/abc-def");
        cut.Find(".md").TextContent.ShouldContain("check Release audit please");
    }
}