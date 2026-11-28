using System.Text.Json;

using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent.Tools;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;

namespace Sandcastle.IntegrationTests;

/// <summary>
/// Sandbox lifecycle/executor tests against the real Docker daemon
/// (SPEC-03 AC). Skip silencioso quando o daemon não está disponível.
/// </summary>
public sealed class DockerSandboxTests : IAsyncLifetime
{
    private const string Image = "bash:5";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-sbx-{Guid.NewGuid():N}.db");
    private readonly string _wsRoot = Path.Combine(
        Path.GetTempPath(), $"sc-ws-{Guid.NewGuid():N}");
    private readonly string _owner = $"test-{Guid.NewGuid():N}";
    private AppDbContext _db = null!;
    private IDockerClient _docker = null!;
    private SandboxService _service = null!;
    private DockerSandboxExecutor _executor = null!;
    private bool _dockerUp;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath};Default Timeout=10")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        try
        {
            _docker = DockerClientFactory.Create();
            await _docker.System.PingAsync();
            _dockerUp = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DOCKER UNAVAILABLE: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            _dockerUp = false;
            return;
        }

        var opts = Options.Create(new SandboxOptions
        {
            WorkspaceRoot = _wsRoot,
            InstanceOwner = _owner,
        });
        var provisioner = new WorkspaceProvisioner(opts,
            NullLogger<WorkspaceProvisioner>.Instance);
        var egress = new DockerNetworkPolicy(_docker, opts,
            NullLogger<DockerNetworkPolicy>.Instance);
        var engine = new DockerSandboxEngine(_docker, provisioner, egress,
            opts, NullLogger<DockerSandboxEngine>.Instance);
        _service = new SandboxService(
            _db, engine, opts, NullLogger<SandboxService>.Instance);
        _executor = new DockerSandboxExecutor(_docker);
    }

    public async Task DisposeAsync()
    {
        _db?.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_wsRoot)) Directory.Delete(_wsRoot, recursive: true);
        await Task.CompletedTask;
    }

    private async Task<Sandcastle.Domain.Sandbox.Sandbox> StartAsync(
        Guid? conversationId = null)
    {
        var spec = new SandboxSpec(Guid.NewGuid(), "test", Image,
            MemoryMb: 128, CpuLimit: 0.5, TimeoutMinutes: 30);
        await _service.UpsertSpecAsync(spec, CancellationToken.None);
        return await _service.StartAsync(
            spec.Id, conversationId ?? Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public async Task Start_Creates_Running_Container_With_Workspace_Mount()
    {
        if (!_dockerUp) return;
        var convId = Guid.NewGuid();
        var sb = await StartAsync(convId);
        try
        {
            sb.Status.ShouldBe(SandboxStatus.Running);
            sb.ContainerId.ShouldNotBeNull();
            var inspect = await _docker.Containers.InspectContainerAsync(sb.ContainerId);
            inspect.State!.Running.ShouldBeTrue();
            inspect.Mounts.Any(m => m.Destination == "/workspace").ShouldBeTrue();
            inspect.Config!.Labels!["sandcastle.conversation_id"]
                .ShouldBe(convId.ToString());
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Exec_Returns_ExitCode_And_Streams()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        try
        {
            var ok = await _executor.ExecAsync(
                sb, "echo hi", TimeSpan.FromSeconds(15), CancellationToken.None);
            ok.ExitCode.ShouldBe(0);
            ok.StdOut.Trim().ShouldBe("hi");

            var bad = await _executor.ExecAsync(
                sb, "exit 3", TimeSpan.FromSeconds(15), CancellationToken.None);
            bad.ExitCode.ShouldBe(3);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Bash_Session_Preserves_State()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        try
        {
            await using var session = await _executor.OpenBashSessionAsync(
                sb, CancellationToken.None);
            (await session.RunAsync("cd /tmp", TimeSpan.FromSeconds(15),
                CancellationToken.None)).ExitCode.ShouldBe(0);
            var pwd = await session.RunAsync("pwd", TimeSpan.FromSeconds(15),
                CancellationToken.None);
            pwd.StdOut.Trim().ShouldBe("/tmp");
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task File_Ops_Round_Trip_In_Workspace()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        try
        {
            await _executor.WriteFileAsync(
                sb, "/workspace/hello.txt", "conteúdo-ç", CancellationToken.None);
            var read = await _executor.ReadFileAsync(
                sb, "/workspace/hello.txt", CancellationToken.None);
            read.ShouldBe("conteúdo-ç");
            var files = await _executor.ListFilesAsync(
                sb, "/workspace", CancellationToken.None);
            files.ShouldContain(f => f.Path == "/workspace/hello.txt" && !f.IsDirectory);
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

    [Fact]
    public async Task Pause_Resume_Delete_Lifecycle()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        var paused = await _service.PauseAsync(sb.Id, CancellationToken.None);
        paused.Status.ShouldBe(SandboxStatus.Paused);
        var resumed = await _service.ResumeAsync(sb.Id, CancellationToken.None);
        resumed.Status.ShouldBe(SandboxStatus.Running);
        await _service.DeleteAsync(sb.Id, CancellationToken.None);
        (await _service.GetAsync(sb.Id, CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<DockerContainerNotFoundException>(() =>
            _docker.Containers.InspectContainerAsync(sb.ContainerId!));
    }

    [Fact]
    public async Task Orphan_Sweep_Removes_Unmanaged_Record_Containers()
    {
        if (!_dockerUp) return;
        // Container com nossa label mas SEM registro no banco = órfão.
        var c = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = Image,
                Cmd = ["sleep", "infinity"],
                Labels = new Dictionary<string, string>
                {
                    ["sandcastle.managed"] = "true",
                    ["sandcastle.owner"] = _owner,
                    ["sandcastle.sandbox_id"] = Guid.NewGuid().ToString(),
                },
            }, CancellationToken.None);
        await _service.SweepOrphansAsync(CancellationToken.None);
        await Should.ThrowAsync<DockerContainerNotFoundException>(() =>
            _docker.Containers.InspectContainerAsync(c.ID));
    }
    private sealed class NoopEventService : IEventService
    {
        public ValueTask<EventBase> AppendAsync(Guid conversationId, EventBase e, CancellationToken ct) => ValueTask.FromResult(e);
        public Task<EventPage> SearchAsync(Guid conversationId, EventQuery q, CancellationToken ct) => Task.FromResult(new EventPage([], null));
        public Task<long> CountAsync(Guid conversationId, EventQuery q, CancellationToken ct) => Task.FromResult(0L);
        public Task<IReadOnlyList<EventBase>> GetBatchAsync(Guid conversationId, IReadOnlyCollection<string> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<EventBase>>([]);
        public async IAsyncEnumerable<EventBase> SubscribeAsync(Guid conversationId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
    }

    private ToolCallContext ToolCtx(Sandcastle.Domain.Sandbox.Sandbox sandbox) => new(
        Guid.NewGuid(), sandbox, _executor, new NoopEventService(),
        new ServiceCollection().BuildServiceProvider(), TimeSpan.FromSeconds(30));

    private static JsonElement ToolArgs(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task Spec182_InstantContext_Readfile_And_Tree_Run_In_Real_Sandbox()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        try
        {
            await _executor.ExecAsync(sb,
                "mkdir -p /workspace/repo/docs && printf 'one\ntwo\nthree\n' > /workspace/repo/docs/a.txt",
                TimeSpan.FromSeconds(15), CancellationToken.None);
            var result = await BuiltinParityTools.InstantContext().Handler(ToolCtx(sb),
                ToolArgs(new { commands = new[] { "readfile docs/a.txt 1 3", "tree docs 1" } }), CancellationToken.None);
            result.IsError.ShouldBeFalse();
            var xml = result.Observation.GetProperty("xml").GetString()!;
            xml.ShouldContain("<ANSWER>");
            xml.ShouldContain("two");
            xml.ShouldContain("docs/a.txt");
        }
        finally
        {
            await _executor.ExecAsync(sb, "chmod -R a+w /workspace 2>/dev/null || true", TimeSpan.FromSeconds(10), CancellationToken.None);
            await _service.DeleteAsync(sb.Id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Spec182_AgentValidate_CustomCommand_Runs_In_Real_Sandbox()
    {
        if (!_dockerUp) return;
        var sb = await StartAsync();
        try
        {
            var result = await BuiltinParityTools.AgentValidate().Handler(ToolCtx(sb),
                ToolArgs(new { command = "echo validation-ok" }), CancellationToken.None);
            result.IsError.ShouldBeFalse();
            result.Observation.GetProperty("validated").GetBoolean().ShouldBeTrue();
            result.Observation.GetProperty("output").GetString()!.ShouldContain("validation-ok");
        }
        finally { await _service.DeleteAsync(sb.Id, CancellationToken.None); }
    }

}