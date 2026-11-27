using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Skills;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Skills;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Skills;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Skills;

/// <summary>SPEC-168 RF-001/003: parser e enforcement de allowed-tools.</summary>
public sealed class Spec168SkillsAllowedToolsTests
{
    [Fact]
    public void ParseFrontmatter_Aceita_Array_E_Csv()
    {
        SkillAllowedTools.ParseFrontmatter("""["file_read", "bash"]""")
            .ShouldBe(["file_read", "bash"]);
        SkillAllowedTools.ParseFrontmatter("file_read, bash")
            .ShouldBe(["file_read", "bash"]);
    }

    [Fact]
    public void ParseSkillMd_Carrega_AllowedTools()
    {
        var parsed = RepoSkillProvider.ParseSkillMd("""
            ---
            name: docs-only
            description: docs
            allowed-tools: ["file_read", "web_fetch"]
            ---

            Use docs.
            """);

        parsed.Name.ShouldBe("docs-only");
        parsed.AllowedTools.ShouldBe(["file_read", "web_fetch"]);
        parsed.Body.ShouldBe("Use docs.");
    }

    [Fact]
    public void Allows_Usa_Uniao_E_Sem_Declaracao_Nao_Restringe()
    {
        var unrestricted = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "open",
            ContentMd = "x",
        };
        SkillAllowedTools.Allows([unrestricted], "bash", out _)
            .ShouldBeTrue();

        var docs = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "docs",
            ContentMd = "x",
            AllowedTools = ["file_read"],
        };
        var shell = new Skill
        {
            Id = Guid.NewGuid(),
            Name = "shell",
            ContentMd = "x",
            AllowedTools = ["bash"],
        };
        SkillAllowedTools.Allows([docs, shell], "bash", out _)
            .ShouldBeTrue();
        SkillAllowedTools.Allows([docs, shell], "file_write",
                out var restricting)
            .ShouldBeFalse();
        restricting.ShouldBe("docs");
    }

    [Fact]
    public async Task Runner_Bloqueia_Tool_Fora_Do_Escopo_Da_Skill()
    {
        const string script = """
            [{"tool":{"name":"bash","arguments":{"command":"echo hi"}}},
             {"tool":{"name":"finish","arguments":{"final_message":"done"}}}]
            """;
        await using var fx = new AgentRunnerFixture(script);
        var db = fx.ScopeServices.GetRequiredService<AppDbContext>();
        db.Skills.Add(new SkillRecord
        {
            Id = Guid.NewGuid(),
            Name = "docs-only",
            Content = "docs",
            TriggersJson = """[{"type":"always"}]""",
            AllowedToolsJson = """["file_read","finish"]""",
            Source = "user",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await fx.Runner.RunAsync(CancellationToken.None);

        fx.Executor.Commands.ShouldNotContain(c => c.Contains("echo hi"));
        fx.Events.Events.OfType<AgentDiagnosticEvent>()
            .ShouldContain(e => e.Code == "tool_blocked_by_skill");
        (await fx.RecordStatusAsync()).ShouldBe("Finished");
    }
}