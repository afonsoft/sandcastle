using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;

using Xunit;

namespace Sandcastle.UnitTests.Conversations;

/// <summary>SPEC-155: agregação do painel operacional (jobs/MCP/info).</summary>
public sealed class ConversationRuntimeServiceTests
{
    private static readonly Guid ConvId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SandboxId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static AppDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new AppDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static ConversationRecord Conv() => new()
    {
        Id = ConvId,
        Title = "t",
        ExecutionStatus = "running",
        LlmModel = "gpt-4o",
        AgentType = "codeact",
        SelectedRepository = "afonsoft/sandcastle",
        SandboxId = SandboxId,
    };

    private static string StateEvent(string key, object value) =>
        JsonSerializer.Serialize(
            new { kind = "ConversationStateUpdateEvent", key, value },
            EventJson.Options);

    private static void AddEvent(AppDbContext db, string payload)
    {
        db.Events.Add(new EventRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = ConvId,
            Kind = "ConversationStateUpdateEvent",
            Source = "environment",
            TimestampTicks = DateTimeOffset.UtcNow.Ticks,
            Payload = payload,
        });
    }

    [Fact]
    public async Task GetAsync_ConversaInexistente_RetornaNull()
    {
        var db = NewDb();
        var svc = new ConversationRuntimeService(
            db, new Sandcastle.UnitTests.Agent.FakeSandboxService(SandboxId),
            new Sandcastle.UnitTests.Agent.FakeSandboxExecutor());
        Assert.Null(await svc.GetAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetAsync_EstadoMcp_UltimoEventoPorServidor()
    {
        var db = NewDb();
        db.Conversations.Add(Conv());
        AddEvent(db, StateEvent("mcp_servers", new
        {
            server = "fs",
            state = "connecting",
            tools = 0,
        }));
        AddEvent(db, StateEvent("mcp_servers", new
        {
            server = "fs",
            state = "connected",
            tools = 12,
        }));
        AddEvent(db, StateEvent("sandbox_rehydrated", new
        {
            rehydrated = true,
        }));
        await db.SaveChangesAsync();

        var svc = new ConversationRuntimeService(
            db, new Sandcastle.UnitTests.Agent.FakeSandboxService(SandboxId),
            new Sandcastle.UnitTests.Agent.FakeSandboxExecutor());
        var dto = await svc.GetAsync(ConvId, default);

        var m = Assert.Single(dto!.McpServers);
        Assert.Equal("fs", m.Name);
        Assert.Equal("connected", m.Status);
        Assert.Equal(12, m.ToolsCount);
        Assert.Equal("gpt-4o", dto.Info.Model);
        Assert.True(dto.Info.Rehydrated);
        Assert.Empty(dto.Jobs); // FakeSandboxExecutor → saída sem linhas |…|
    }
}