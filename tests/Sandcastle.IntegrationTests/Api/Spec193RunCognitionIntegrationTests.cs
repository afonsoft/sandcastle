using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-193 RF-008: composed-host registry and event persistence coverage.</summary>
public sealed class Spec193RunCognitionIntegrationTests
{
    [Fact]
    public void Composed_host_resolves_run_cognition_tools()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.Find("goal").ShouldNotBeNull();
        registry.Find("wait").ShouldNotBeNull();
        registry.Find("memory_reflect").ShouldNotBeNull();
    }

    [Fact]
    public async Task Run_goal_and_advisor_events_round_trip_with_machine_payloads()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<IEventService>();
        var conversationId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = conversationId,
            ExecutionStatus = "ready",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await events.AppendAsync(conversationId, new RunGoalEvent
        {
            Objective = "Ship SPEC-193",
            ToolCallId = "call-goal",
        }, CancellationToken.None);
        await events.AppendAsync(conversationId, new AdvisorEvent
        {
            Question = "safe?",
            Answer = "severity: blocker\nstop",
            Severity = "blocker",
        }, CancellationToken.None);

        var goalPage = await events.SearchAsync(conversationId,
            new EventQuery(KindEq: "run_goal"), CancellationToken.None);
        var goal = goalPage.Items.OfType<RunGoalEvent>().ShouldHaveSingleItem();
        goal.Objective.ShouldBe("Ship SPEC-193");
        goal.ToolCallId.ShouldBe("call-goal");

        var advisorPage = await events.SearchAsync(conversationId,
            new EventQuery(KindEq: "advisor"), CancellationToken.None);
        advisorPage.Items.OfType<AdvisorEvent>().ShouldHaveSingleItem()
            .Severity.ShouldBe("blocker");
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString",
                $"Data Source={Path.Combine(Path.GetTempPath(), $"sc193-{Guid.NewGuid():N}.db")}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
        });
}