using Sandcastle.Application.Automations;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests;

/// <summary>
/// SPEC-160 RF-004: <see cref="AutomationSchedule.NextRuns"/> — projeção
/// da agenda dentro de [from, to] para os 4 kinds, limites e "once".
/// </summary>
public sealed class Spec160NextRunsTests
{
    private static readonly DateTimeOffset From =
        new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero); // sábado
    private static readonly DateTimeOffset To = From.AddDays(3);

    [Fact]
    public void Interval_Expande_Ate_O_Limite()
    {
        var runs = AutomationSchedule.NextRuns(
            "interval", null, 60, null, null, From, To);
        // cursor avança +1 tick por iteração → o 72º passa 1 tick do fim.
        runs.Count.ShouldBe(71);
        runs[0].ShouldBe(From.AddHours(1));
        runs[^1].ShouldBeLessThanOrEqualTo(To);
    }

    [Fact]
    public void Daily_Uma_Por_Dia()
    {
        var runs = AutomationSchedule.NextRuns(
            "daily", "09:00", null, null, null, From, To);
        runs.Count.ShouldBe(3);
        runs[0].ShouldBe(From.AddHours(9));
    }

    [Fact]
    public void Weekly_Dentro_Da_Janela_Ou_Vazio()
    {
        // domingo(0) 09:00 → único disparo dentro de sáb-ter
        AutomationSchedule.NextRuns(
            "weekly", "09:00", null, 0, null, From, To)
            .ShouldBe([From.AddDays(1).AddHours(9)]);
        // segunda(1) 09:00 está fora da janela → vazio
        AutomationSchedule.NextRuns(
            "weekly", "09:00", null, 1, null,
            From, From.AddDays(1))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Once_No_Maximo_Uma_Ocorrencia()
    {
        var at = From.AddHours(5);
        AutomationSchedule.NextRuns(
            "once", null, null, null, at, From, To)
            .ShouldBe([at]);
        // passado → nada; sem stored → nada
        AutomationSchedule.NextRuns(
            "once", null, null, null, From.AddHours(-1), From, To)
            .ShouldBeEmpty();
        AutomationSchedule.NextRuns(
            "once", null, null, null, null, From, To)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Max_Cap_Respeitado() =>
        AutomationSchedule.NextRuns(
            "interval", null, 1, null, null, From, To, max: 5)
            .Count.ShouldBe(5);

    [Fact]
    public void Schedule_Invalida_Retorna_Vazio()
    {
        AutomationSchedule.NextRuns(
            "daily", "banana", null, null, null, From, To)
            .ShouldBeEmpty();
        AutomationSchedule.NextRuns(
            "cron", "10:00", 10, 0, null, From, To)
            .ShouldBeEmpty();
    }
}