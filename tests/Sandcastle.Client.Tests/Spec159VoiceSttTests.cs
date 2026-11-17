using System.Net;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

/// <summary>
/// SPEC-159: ditado por voz (STT) — VoiceButton feature-detect, estados
/// listening/erro via interop mockado e concat no texto do composer.
/// Mic real não é testável — guard manual na SPEC.
/// </summary>
public class Spec159VoiceSttTests : BunitContext
{
    public Spec159VoiceSttTests()
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

    private void RegisterApi()
    {
        Services.AddSingleton(new SandcastleApiClient(new HttpClient(
            new StubHandler(_ => Json("[]")))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        }));
    }

    private void MockSupported(bool supported) =>
        JSInterop.Setup<bool>("scSpeech.supported")
            .SetResult(supported);

    private void MockStart(bool started) =>
        JSInterop.Setup<bool>(i => i.Identifier == "scSpeech.start")
            .SetResult(started);

    // ---- VoiceButton ---------------------------------------------------

    [Fact]
    public void VoiceButton_Hidden_When_Unsupported()
    {
        MockSupported(false);
        var cut = Render<VoiceButton>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".voice-btn").ShouldBeEmpty(),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void VoiceButton_Toggles_Listening_And_Stop()
    {
        MockSupported(true);
        MockStart(true);
        var cut = Render<VoiceButton>(p => p
            .Add(x => x.OnDictationStarted, () => { })
            .Add(x => x.OnDictationEnded, () => { }));

        cut.WaitForAssertion(() =>
            cut.Find(".voice-btn").Click(),
            TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() =>
        {
            cut.Find(".voice-btn.listening").ShouldNotBeNull();
            cut.Find(".voice-live").ShouldNotBeNull();
        }, TimeSpan.FromSeconds(2));

        // Segundo clique → scSpeech.stop.
        cut.Find(".voice-btn").Click();
        JSInterop.VerifyInvoke("scSpeech.stop");
    }

    [Fact]
    public void VoiceButton_Unsupported_Start_Reports_Error()
    {
        MockSupported(true);
        MockStart(false);
        string? err = null;
        var cut = Render<VoiceButton>(p => p
            .Add(x => x.OnError, (string e) => err = e));

        cut.WaitForAssertion(() =>
            cut.Find(".voice-btn").Click(),
            TimeSpan.FromSeconds(2));

        err.ShouldBe("unsupported");
        cut.Instance.IsListening.ShouldBeFalse();
    }

    // ---- Composer integration ------------------------------------------

    [Fact]
    public async Task Composer_Appends_Transcript_To_Existing_Text()
    {
        MockSupported(true);
        MockStart(true);
        RegisterApi();
        var cut = Render<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, _ => { }));

        cut.Find("textarea").Input("base");
        cut.WaitForAssertion(() =>
            cut.Find(".voice-btn").Click(),
            TimeSpan.FromSeconds(3));

        var voice = cut.FindComponent<VoiceButton>();
        await cut.InvokeAsync(() => voice.Instance.OnSttResult("ditado aqui"));

        cut.Find("textarea").GetAttribute("value")!
            .ShouldBe("base ditado aqui");
        // Não enviou sozinho — draft continua no input.
        cut.Find(".send-btn").HasAttribute("disabled")
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Composer_MicDenied_Does_Not_Alter_Text()
    {
        MockSupported(true);
        MockStart(true);
        RegisterApi();
        var cut = Render<ChatComposer>(p => p
            .Add(x => x.ConversationId, "conv-1")
            .Add(x => x.OnSend, _ => { }));

        cut.Find("textarea").Input("keep");
        cut.WaitForAssertion(() =>
            cut.Find(".voice-btn").Click(),
            TimeSpan.FromSeconds(3));

        var voice = cut.FindComponent<VoiceButton>();
        await cut.InvokeAsync(() => voice.Instance.OnSttError("not-allowed"));

        cut.Find("textarea").GetAttribute("value")!.ShouldBe("keep");
        voice.Instance.IsListening.ShouldBeFalse();
    }
}