using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.Routing;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-167 RF-002: matemática EMA + fator de custo puros.</summary>
public sealed class Spec167ModelStatsMathTests
{
    [Fact]
    public void Quality_Ema_Converges_To_Success()
    {
        var ema = ModelStatsMath.InitialQuality;
        for (var i = 0; i < 50; i++)
        {
            ema = ModelStatsMath.NextQuality(ema, success: true);
        }

        ema.ShouldBeGreaterThan(0.85);
    }

    [Fact]
    public void Quality_Ema_Drops_On_Failure()
    {
        var ema = ModelStatsMath.NextQuality(
            ModelStatsMath.InitialQuality, success: false);

        ema.ShouldBeLessThan(0.5);
        ema.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void Latency_Ema_First_Sample_Wins()
    {
        ModelStatsMath.NextLatency(null, 200).ShouldBe(200);
        ModelStatsMath.NextLatency(200, 100)
            .ShouldBeLessThan(200);
    }

    [Fact]
    public void CostFactor_Cheapest_Is_One()
    {
        var cheap = new RouteCandidate("a", PriceInPer1M: 1m);
        var pricey = new RouteCandidate("b", PriceInPer1M: 4m);
        ModelStatsMath.CostFactor(cheap, 1m).ShouldBe(1.0);
        ModelStatsMath.CostFactor(pricey, 1m).ShouldBe(0.25, 0.001);
        ModelStatsMath.CostFactor(new RouteCandidate("c"), 1m)
            .ShouldBe(1.0); // sem preço ⇒ neutro
    }

    [Fact]
    public void Key_Normalizes_Null_Provider()
    {
        ModelStatsMath.Key(null, "m").ShouldBe(":m");
        ModelStatsMath.Key("p", "m").ShouldBe("p:m");
    }
}

/// <summary>SPEC-167 RF-002: score adaptativo na ordenação (weight lottery).</summary>
public sealed class Spec167AdaptiveEvaluatorTests
{
    private static RoutingRulesDocument Doc(
        params RouteCandidate[] candidates) =>
        new()
        {
            Rules = new Dictionary<string, List<RouteCandidate>>
            {
                ["code"] = [.. candidates],
            },
        };

    [Fact]
    public void Low_Quality_Candidate_Loses_Lottery()
    {
        // a e b com weight 1; "a" tem quality_ema ~0 ⇒ "b" vence ~99%
        // dos cursores (peso efetivo de "a" ≈ 0).
        var scores = new Dictionary<string, double>
        {
            [ModelStatsMath.Key("p", "a")] = 0.01,
            [ModelStatsMath.Key("p", "b")] = 1.0,
        };
        var doc = Doc(
            new RouteCandidate("a", "p", Weight: 1),
            new RouteCandidate("b", "p", Weight: 1));

        var bWins = 0;
        for (var cursor = 0; cursor < 1000; cursor++)
        {
            var r = RouteEvaluator.Evaluate(
                doc.CandidatesFor("code")!, doc, pinnable: false,
                pin: null, remainingBudgetPct: null,
                cheapWhenBelowPct: 20, new LlmCircuitBreaker(),
                cursor, scores);
            if (r.Ordered[0].Model == "b")
            {
                bWins++;
            }
        }

        bWins.ShouldBeGreaterThan(950);
    }

    [Fact]
    public void Absent_Stats_Are_Neutral()
    {
        // Sem scores ⇒ ordem declarada (comportamento SPEC-149 intacto).
        var doc = Doc(
            new RouteCandidate("a", "p", Weight: 1),
            new RouteCandidate("b", "p", Weight: 1));

        var r = RouteEvaluator.Evaluate(
            doc.CandidatesFor("code")!, doc, pinnable: false,
            pin: null, remainingBudgetPct: null, cheapWhenBelowPct: 20,
            new LlmCircuitBreaker(), weightCursor: 0,
            adaptiveScores: null);
        r.Ordered[0].Model.ShouldBe("a");
    }

    [Fact]
    public void Price_Spread_Prefers_Cheap()
    {
        // Mesmo weight/qualidade: o mais barato ganha ~89% da loteria
        // (cost_factor 1/8 ⇒ ef 0.125 vs 1.0).
        var doc = Doc(
            new RouteCandidate("a", "p", Weight: 1, PriceInPer1M: 8m),
            new RouteCandidate("b", "p", Weight: 1, PriceInPer1M: 1m));
        var scores = new Dictionary<string, double>
        {
            [ModelStatsMath.Key("p", "a")] = 1.0,
            [ModelStatsMath.Key("p", "b")] = 1.0,
        };

        var bWins = 0;
        for (var cursor = 0; cursor < 1000; cursor++)
        {
            var r = RouteEvaluator.Evaluate(
                doc.CandidatesFor("code")!, doc, pinnable: false,
                pin: null, remainingBudgetPct: null, cheapWhenBelowPct: 20,
                new LlmCircuitBreaker(), cursor, scores);
            if (r.Ordered[0].Model == "b")
            {
                bWins++;
            }
        }

        bWins.ShouldBeGreaterThan(850);
    }
}