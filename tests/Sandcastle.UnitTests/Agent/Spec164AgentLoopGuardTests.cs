using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent;

using Shouldly;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-164 RF-001: pure same-tool/repeat-args guard behavior.</summary>
public sealed class Spec164AgentLoopGuardTests
{
    [Fact]
    public void RepeatArgs_Veta_Na_Terceira_Call_Identica()
    {
        var guard = new AgentLoopGuard(maxSameToolConsecutive: 6, maxRepeatArgs: 3);
        guard.Register("file_read", """{"path":"a.cs"}""").ShouldBe(LoopVerdict.Ok);
        guard.Register("file_read", """{"path":"a.cs"}""").ShouldBe(LoopVerdict.Ok);
        guard.Register("file_read", """{"path":"a.cs"}""")
            .ShouldBe(LoopVerdict.VetoRepeatArgs);
    }

    [Fact]
    public void SameTool_Veta_No_Limite_Mesmo_Com_Args_Diferentes()
    {
        var guard = new AgentLoopGuard(maxSameToolConsecutive: 3, maxRepeatArgs: 10);
        guard.Register("file_read", """{"path":"a"}""").ShouldBe(LoopVerdict.Ok);
        guard.Register("file_read", """{"path":"b"}""").ShouldBe(LoopVerdict.Ok);
        guard.Register("file_read", """{"path":"c"}""")
            .ShouldBe(LoopVerdict.VetoSameTool);
    }

    [Fact]
    public void Trocar_Tool_Reseta_As_Duas_Sequencias()
    {
        var guard = new AgentLoopGuard(maxSameToolConsecutive: 3, maxRepeatArgs: 3);
        guard.Register("file_read", """{"path":"a"}""");
        guard.Register("file_read", """{"path":"a"}""");
        guard.Register("bash", """{"command":"ls"}""").ShouldBe(LoopVerdict.Ok);
        guard.Register("file_read", """{"path":"a"}""").ShouldBe(LoopVerdict.Ok);
    }

    [Fact]
    public void Args_Canonicos_Ignoram_Ordem_De_Propriedades()
    {
        AgentLoopGuard.CanonicalArgsHash("""{"b":2,"a":{"d":4,"c":3}}""")
            .ShouldBe(AgentLoopGuard.CanonicalArgsHash("""{"a":{"c":3,"d":4},"b":2}"""));
    }
}

/// <summary>SPEC-164 RF-002/003/004: runner integration through MockLlm.</summary>
public sealed class Spec164AgentLoopGuardRunnerTests
{
    [Fact]
    public async Task RepeatArgs_No_Runner_Emite_LoopGuardVeto_E_Recupera()
    {
        const string script = """
            [{"tool":{"name":"think","arguments":{"thought":"same"}}},
             {"tool":{"name":"think","arguments":{"thought":"same"}}},
             {"tool":{"name":"think","arguments":{"thought":"same"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"recovered"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            new AgentLlmOptions { MaxRepeatArgs = 3, MaxSameToolConsecutive = 10 });
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<Sandcastle.Domain.Events.AgentDiagnosticEvent>()
            .ShouldContain(e => e.Code == "loop_guard_veto");
        fx.Events.Events.OfType<Sandcastle.Domain.Events.MessageEvent>()
            .ShouldContain(e => e.LlmMessage.ToString().Contains("recovered"));
    }

    [Fact]
    public async Task SameTool_No_Runner_Veta_No_Limite()
    {
        const string script = """
            [{"tool":{"name":"think","arguments":{"thought":"a"}}},
             {"tool":{"name":"think","arguments":{"thought":"b"}}},
             {"tool":{"name":"think","arguments":{"thought":"c"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script,
            new AgentLlmOptions { MaxSameToolConsecutive = 3, MaxRepeatArgs = 10 });
        await fx.Runner.RunAsync(CancellationToken.None);

        var diagnostic = fx.Events.Events
            .OfType<Sandcastle.Domain.Events.AgentDiagnosticEvent>()
            .Single(e => e.Code == "loop_guard_veto");
        diagnostic.Message.ShouldContain("change approach");
    }

    [Fact]
    public async Task EmptyCompletion_Emite_TurnEndAnomaly_E_Mensagem_Visivel()
    {
        await using var fx = new AgentRunnerFixture("[{}]");
        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Events.Events.OfType<Sandcastle.Domain.Events.AgentDiagnosticEvent>()
            .ShouldContain(e => e.Code == "turn_end_anomaly");
        fx.Events.Events.OfType<Sandcastle.Domain.Events.MessageEvent>()
            .ShouldContain(e => e.LlmMessage.ToString()
                .Contains("Run encerrada pelo guard de turn-end"));
    }

    [Fact]
    public async Task StallWatchdog_Emite_Nudge_E_Com_Maximo_Zero_Aborota()
    {
        await using var nudgeFx = new AgentRunnerFixture(
            """[{"tool":{"name":"finish","arguments":{"final_message":"ok"}}}]""",
            new AgentLlmOptions { StallWatchdogSec = 0, MaxStallNudges = 1 });
        await nudgeFx.Runner.RunAsync(CancellationToken.None);
        nudgeFx.Events.Events.OfType<Sandcastle.Domain.Events.AgentDiagnosticEvent>()
            .ShouldContain(e => e.Code == "agent_stall");

        await using var abortFx = new AgentRunnerFixture(
            """[{"tool":{"name":"finish","arguments":{"final_message":"late"}}}]""",
            new AgentLlmOptions { StallWatchdogSec = 0, MaxStallNudges = 0 });
        await abortFx.Runner.RunAsync(CancellationToken.None);
        abortFx.Events.Events.OfType<Sandcastle.Domain.Events.AgentErrorEvent>()
            .ShouldContain(e => e.Error == "stalled");
    }
}