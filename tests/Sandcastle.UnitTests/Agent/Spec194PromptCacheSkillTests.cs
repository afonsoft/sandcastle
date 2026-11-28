using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Agent;
using Sandcastle.Domain.Skills;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Skills;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-194: cache-tier composition, path triggers and progressive skills.</summary>
public sealed class Spec194PromptCacheSkillTests
{
    private static readonly ToolSchema Bash = new(
        "bash", "run shell commands", JsonDocument.Parse("{}").RootElement);

    private sealed class TieredStub(
        int order, string section, PromptCacheTier tier)
        : IAgentPromptContributor
    {
        public int Order => order;
        public PromptCacheTier CacheTier => tier;
        public Task<IReadOnlyList<string>> ContributeAsync(
            AgentPromptContext ctx) =>
            Task.FromResult<IReadOnlyList<string>>([section]);
    }

    private static AgentPromptContext Ctx() => new()
    {
        Tools = [Bash],
        Ct = CancellationToken.None,
    };

    [Fact]
    public async Task Composer_emits_static_sections_before_dynamic_sections()
    {
        var composer = new SystemPromptComposer(
        [
            new TieredStub(0, "dynamic-first-order", PromptCacheTier.Dynamic),
            new TieredStub(90, "static-late-order", PromptCacheTier.Static),
            new TieredStub(10, "static-early-order", PromptCacheTier.Static),
        ], NullLogger<SystemPromptComposer>.Instance);

        var composition = await composer.ComposeDetailedAsync(Ctx());

        composition.Text.ShouldBe(
            "static-early-order\n\nstatic-late-order\n\ndynamic-first-order\n");
        composition.StaticChars.ShouldBe(
            "static-early-order".Length + "static-late-order".Length);
        composition.DynamicChars.ShouldBe("dynamic-first-order".Length);
        composition.Sections.Select(s => s.Tier).ShouldBe([
            PromptCacheTier.Static, PromptCacheTier.Static, PromptCacheTier.Dynamic,
        ]);
    }

    [Fact]
    public async Task Catalog_contributor_omits_injected_and_bounds_noninjected_entries()
    {
        static Skill Skill(string name, string description) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            ContentMd = "full body must not appear",
            Enabled = true,
        };
        var injected = Skill("injected", "already loaded");
        var advertised = Skill("available-one", "useful catalog entry");
        var ctx = new AgentPromptContext
        {
            Tools = [Bash],
            Skills = [injected],
            SkillCatalog = [injected, advertised],
            Ct = CancellationToken.None,
        };

        var sections = await new SkillCatalogContributor().ContributeAsync(ctx);

        var section = sections.ShouldHaveSingleItem();
        section.ShouldContain("`available-one`");
        section.ShouldContain("useful catalog entry");
        section.ShouldNotContain("`injected`");
        section.ShouldNotContain("full body must not appear");
        section.Length.ShouldBeLessThanOrEqualTo(2100);
    }

    [Fact]
    public async Task Path_trigger_matches_workspace_paths_and_referenced_text_only()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sc-spec194-{Guid.NewGuid():N}.db");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await db.Database.MigrateAsync();
        var svc = new SkillService(db);
        await svc.CreateAsync(new Skill
        {
            Id = Guid.NewGuid(),
            Name = "frontend-path",
            ContentMd = "x",
            Triggers = [new SkillTrigger(SkillTriggerType.Path, "src/web/**")],
        }, CancellationToken.None);
        var conv = Guid.NewGuid();

        var touched = await svc.ResolveForConversationAsync(
            conv, null, null, null, null, CancellationToken.None,
            ["src/web/app.ts"]);
        touched.Select(s => s.Name).ShouldBe(["frontend-path"]);

        var referenced = await svc.ResolveForConversationAsync(
            conv, "please update src/web/app.ts", null, null, null,
            CancellationToken.None);
        referenced.Select(s => s.Name).ShouldBe(["frontend-path"]);

        var unrelated = await svc.ResolveForConversationAsync(
            conv, "please update docs/readme.md", null, null, null,
            CancellationToken.None);
        unrelated.ShouldBeEmpty();
    }
}