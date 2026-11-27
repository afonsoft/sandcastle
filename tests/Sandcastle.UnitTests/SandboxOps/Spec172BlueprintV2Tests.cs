using System.Text.Json;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Agent.SystemPrompt;
using Sandcastle.Infrastructure.Sandbox;

namespace Sandcastle.UnitTests.SandboxOps;

/// <summary>
/// SPEC-172 unit coverage: canonical <c>.agents/blueprint.yaml</c>
/// discovery + precedence, schema v2 (<c>initialize</c>,
/// <c>maintenance</c>, <c>knowledge</c>, <c>secrets</c>), knowledge
/// serialization round-trip and the prompt contributor. Pure host-side
/// logic — no docker.
/// </summary>
public sealed class Spec172BlueprintV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"sc-spec172-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Parse_V2_Full_Yaml_Returns_All_Fields()
    {
        var spec = BlueprintService.Parse("""
            image: ghcr.io/sandcastle/runtime:v2
            env: { DOTNET_CLI_TELEMETRY_OPTOUT: "1" }
            initialize: |
              dotnet tool install -g dotnet-ef
            maintenance: |
              dotnet restore Sandcastle.slnx
            knowledge:
              - name: build
                contents: |
                  # dotnet build Sandcastle.slnx -c Release
              - name: test
                contents: |
                  # dotnet test
            secrets: [NUGET_TOKEN, GH_PAT]
            """);
        spec.ShouldNotBeNull();
        spec.Image.ShouldBe("ghcr.io/sandcastle/runtime:v2");
        spec.InitializeScript.ShouldNotBeNull()
            .ShouldContain("dotnet-ef");
        spec.MaintenanceScript.ShouldNotBeNull()
            .ShouldContain("dotnet restore");
        spec.Knowledge.ShouldNotBeNull();
        spec.Knowledge.Count.ShouldBe(2);
        spec.Knowledge[0].Name.ShouldBe("build");
        spec.Knowledge[0].Contents.ShouldContain("dotnet build");
        spec.Knowledge[1].Name.ShouldBe("test");
        spec.Secrets.ShouldBe(["NUGET_TOKEN", "GH_PAT"]);
    }

    [Fact]
    public void Parse_V1_Yaml_Still_Works_And_V2_Fields_Null()
    {
        var spec = BlueprintService.Parse("""
            sdk: [ "dotnet:10" ]
            setup: .openhands/bootstrap.sh
            """);
        spec.ShouldNotBeNull();
        spec.SetupScript.ShouldBe(".openhands/bootstrap.sh");
        spec.InitializeScript.ShouldBeNull();
        spec.MaintenanceScript.ShouldBeNull();
        spec.Knowledge.ShouldBeNull();
        spec.Secrets.ShouldBeNull();
    }

    [Fact]
    public void Parse_Knowledge_Skips_Non_Map_And_Missing_Contents()
    {
        var spec = BlueprintService.Parse("""
            knowledge:
              - "just-a-string"
              - name: nope
              - name: ok
                contents: "works"
            """);
        spec.ShouldNotBeNull();
        spec.Knowledge.ShouldNotBeNull();
        spec.Knowledge.ShouldBe(
            [new BlueprintKnowledge("ok", "works")]);
    }

    [Fact]
    public void FindBlueprintFile_Prefers_Agents_Over_Legacy()
    {
        var agents = Path.Combine(_dir, ".agents");
        var devin = Path.Combine(_dir, ".devin");
        Directory.CreateDirectory(agents);
        Directory.CreateDirectory(devin);
        File.WriteAllText(Path.Combine(agents, "blueprint.yaml"), "image: a");
        File.WriteAllText(Path.Combine(_dir, "sandcastle.yaml"), "image: s");
        File.WriteAllText(Path.Combine(devin, "blueprint.yaml"), "image: d");

        BlueprintService.FindBlueprintFile(_dir)
            .ShouldBe(Path.Combine(agents, "blueprint.yaml"));
    }

    [Fact]
    public void FindBlueprintFile_Falls_Back_To_Sandcastle_Then_Devin()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sandcastle.yaml"), "image: s");
        var devin = Path.Combine(_dir, ".devin");
        Directory.CreateDirectory(devin);
        File.WriteAllText(Path.Combine(devin, "blueprint.yaml"), "image: d");

        BlueprintService.FindBlueprintFile(_dir)
            .ShouldBe(Path.Combine(_dir, "sandcastle.yaml"));

        File.Delete(Path.Combine(_dir, "sandcastle.yaml"));
        BlueprintService.FindBlueprintFile(_dir)
            .ShouldBe(Path.Combine(devin, "blueprint.yaml"));
    }

    [Fact]
    public void FindBlueprintFile_None_Returns_Null()
    {
        Directory.CreateDirectory(_dir);
        BlueprintService.FindBlueprintFile(_dir).ShouldBeNull();
        BlueprintService.FindBlueprintFile(
            Path.Combine(_dir, "missing")).ShouldBeNull();
    }

    [Fact]
    public void Knowledge_Json_RoundTrips()
    {
        var notes = new List<BlueprintKnowledge>
        {
            new("build", "dotnet build -c Release"),
            new("test", "dotnet test"),
        };
        var json = JsonSerializer.Serialize(
            notes, BlueprintService.KnowledgeJsonOptions);
        json.ShouldContain("\"name\"");
        var parsed = BlueprintService.DeserializeKnowledge(json);
        parsed.ShouldBe(notes);
    }

    [Fact]
    public void DeserializeKnowledge_Invalid_Json_Returns_Empty()
    {
        BlueprintService.DeserializeKnowledge("{not json").ShouldBeEmpty();
    }

    [Fact]
    public async Task KnowledgeContributor_Emits_Section_When_Present()
    {
        var ctx = new AgentPromptContext
        {
            Tools = [],
            Ct = CancellationToken.None,
            BlueprintKnowledge =
                [("build", "dotnet build"), ("test", "dotnet test")],
        };
        var sections = await new BlueprintKnowledgeContributor()
            .ContributeAsync(ctx);
        sections.Count.ShouldBe(1);
        sections[0].ShouldContain("## Environment blueprint");
        sections[0].ShouldContain("### build");
        sections[0].ShouldContain("dotnet test");
    }

    [Fact]
    public async Task KnowledgeContributor_Silent_When_Empty()
    {
        var ctx = new AgentPromptContext
        {
            Tools = [],
            Ct = CancellationToken.None,
        };
        var sections = await new BlueprintKnowledgeContributor()
            .ContributeAsync(ctx);
        sections.ShouldBeEmpty();
    }
}