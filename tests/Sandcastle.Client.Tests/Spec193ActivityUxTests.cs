using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Client.Components.Conversation;
using Sandcastle.Client.Services;

using Shouldly;

namespace Sandcastle.Client.Tests;

public sealed class Spec193ActivityUxTests : BunitContext
{
    public Spec193ActivityUxTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new MarkdownService());
        L10nStub.Register(this);
    }

    private static ClientEvent Ev(string json) => new()
    {
        Raw = JsonDocument.Parse(json).RootElement.Clone(),
    };

    [Fact]
    public void Preview_preserves_diff_lines()
    {
        var preview = ToolOutputPreview.Preview("noise\n@@ -1 +1 @@\n-old\n+new\nnoise");
        preview.ShouldContain("@@ -1 +1 @@");
        preview.ShouldContain("-old");
        preview.ShouldContain("+new");
        preview.ShouldNotContain("noise");
    }

    [Fact]
    public void Structured_summary_interpolates_counts_paths_and_preserves_patch_text()
    {
        using var counts = JsonDocument.Parse("""{"count":3,"path":"src/App.cs"}""");
        ToolOutputPreview.Summary(counts.RootElement).ShouldBe("3 items · src/App.cs");
        using var patch = JsonDocument.Parse("""{"patch":"@@ -1 +1 @@\n-old\n+new"}""");
        ToolOutputPreview.Summary(patch.RootElement)!.ShouldContain("+new");
    }

    [Fact]
    public void Structured_summary_combines_bounded_path_fields()
    {
        using var doc = JsonDocument.Parse("""{"archive":"bundle.tar","job_id":"job-1","key":"alpha"}""");
        ToolOutputPreview.Summary(doc.RootElement).ShouldBe("bundle.tar · job-1 · alpha");
    }

    [Fact]
    public void Activity_map_humanizes_cognition_tools_with_targets()
    {
        ToolActivityMap.Map("goal", "{\"objective\":\"Ship parity\"}")
            .ShouldBe(("chat.activity.goal", "Ship parity"));
        ToolActivityMap.Map("wait", "{\"job_id\":\"job-1\"}")
            .ShouldBe(("chat.activity.wait", "job-1"));
        ToolActivityMap.Map("memory_reflect", "{\"query\":\"canonical\"}")
            .ShouldBe(("chat.activity.memory", "canonical"));
    }

    [Fact]
    public void Run_goal_and_advisor_render_machine_readable_activity()
    {
        var goal = Ev("""{"id":"g1","kind":"run_goal","source":"agent","timestamp":"2026-01-01T00:00:00Z","objective":"Ship parity"}""");
        var advisor = Ev("""{"id":"ad1","kind":"advisor","source":"environment","timestamp":"2026-01-01T00:00:00Z","severity":"blocker","answer":"Do not proceed"}""");

        Render<EventCard>(p => p.Add(x => x.Event, goal)).Markup.ShouldContain("Ship parity");
        var adv = Render<EventCard>(p => p.Add(x => x.Event, advisor));
        adv.Markup.ShouldContain("Do not proceed");
        adv.Markup.ShouldContain("advisor-blocker");
    }

    [Fact]
    public void Reloaded_observation_restores_tool_name_and_collapsed_preview()
    {
        var obs = Ev("""{"id":"o1","kind":"ObservationEvent","source":"agent","timestamp":"2026-01-01T00:00:00Z","tool_name":"github_view_pull_request","observation":{"output":"@@ -1 +1 @@\n-old\n+new"}}""");
        var cut = Render<EventCard>(p => p.Add(x => x.Event, obs));
        cut.Markup.ShouldContain("github_view_pull_request");
        cut.Find(".obs-preview").TextContent.ShouldContain("+new");
    }
}