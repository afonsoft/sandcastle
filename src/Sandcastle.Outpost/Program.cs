// SPEC-153 RF-003: sandcastle-outpost — self-hosted sandbox runner.
// Outbound-only: opens ONE WebSocket to the control plane; never
// listens inbound. Secrets cross the channel by NAME only — values
// resolve locally from env (SC_OUTPOST_SECRET_<NAME>).

using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Docker.DotNet;
using Docker.DotNet.Models;

using Sandcastle.Application.Outpost;
using Sandcastle.Outpost;


var cmd = args.FirstOrDefault() ?? "run";
return cmd switch
{
    "enroll" => await EnrollAsync(args[1..]),
    "doctor" => await DoctorAsync(),
    "run" => await RunAsync(),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        "usage: sandcastle-outpost <enroll|run|doctor> [args]");
    return 2;
}

// ---------------- enroll ----------------

static async Task<int> EnrollAsync(string[] a)
{
    // enroll <plane-url> <enroll-token> [--name n] [--label k=v]*
    if (a.Length < 2)
    {
        Console.Error.WriteLine(
            "usage: sandcastle-outpost enroll <plane-url> <token> " +
            "[--name n] [--label k=v]");
        return 2;
    }

    var labels = new Dictionary<string, string>();
    string? name = null;
    for (var i = 2; i < a.Length; i++)
    {
        if (a[i] == "--name" && i + 1 < a.Length) name = a[++i];
        else if (a[i] == "--label" && i + 1 < a.Length
            && a[i + 1].Split('=', 2) is { Length: 2 } kv)
        {
            labels[kv[0]] = kv[1];
            i++;
        }
    }

    var plane = a[0].TrimEnd('/');
    using var http = new HttpClient { BaseAddress = new Uri(plane) };
    var resp = await http.PostAsJsonAsync(
        "/api/v1/outposts/register", new
        {
            enroll_token = a[1],
            name,
            labels = labels.Count > 0 ? labels : null,
            version = ThisAssemblyVersion(),
        });
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine(
            $"enroll failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return 1;
    }

    var reg = await resp.Content
        .ReadFromJsonAsync<RegisterResponse>();
    if (reg is null)
    {
        Console.Error.WriteLine("enroll failed: empty response");
        return 1;
    }

    Directory.CreateDirectory(ConfigDir());
    var cfg = new OutpostConfig
    {
        OutpostId = reg.OutpostId,
        ChannelKey = reg.ChannelKey,
        ConnectUrl = plane + "/api/v1/outposts/connect",
        HeartbeatSec = reg.HeartbeatSec > 0 ? reg.HeartbeatSec : 30,
    };
    if (reg.CertPem is not null && reg.KeyPem is not null)
    {
        cfg.CertPath = Path.Combine(ConfigDir(), "client.crt");
        cfg.KeyPath = Path.Combine(ConfigDir(), "client.key");
        await File.WriteAllTextAsync(cfg.CertPath, reg.CertPem);
        await File.WriteAllTextAsync(cfg.KeyPath, reg.KeyPem);
        Chmod600(cfg.KeyPath);
    }

    var tmp = ConfigPath() + ".tmp";
    await File.WriteAllTextAsync(tmp,
        JsonSerializer.Serialize(cfg, JsonOpts.CfgJson));
    File.Move(tmp, ConfigPath(), overwrite: true);
    Chmod600(ConfigPath());
    Console.WriteLine($"enrolled: outpost {reg.OutpostId} → {cfg.ConnectUrl}");
    return 0;
}

// ---------------- doctor ----------------

static async Task<int> DoctorAsync()
{
    var ok = true;
    if (!File.Exists(ConfigPath()))
    {
        Console.Error.WriteLine($"FAIL: no config at {ConfigPath()} — run enroll first");
        ok = false;
    }
    else
    {
        Console.WriteLine($"config: {ConfigPath()}");
    }

    try
    {
        using var docker = NewDocker();
        var v = await docker.System.GetVersionAsync(default);
        Console.WriteLine($"docker: {v.Version} ({v.APIVersion})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL: docker — {ex.Message}");
        ok = false;
    }

    return ok ? 0 : 1;
}

// ---------------- run ----------------

static async Task<int> RunAsync()
{
    if (!File.Exists(ConfigPath()))
    {
        Console.Error.WriteLine(
            $"no config at {ConfigPath()} — run `sandcastle-outpost enroll` first");
        return 1;
    }

    var cfg = JsonSerializer.Deserialize<OutpostConfig>(
        await File.ReadAllTextAsync(ConfigPath()), JsonOpts.CfgJson)!;

    var backoff = TimeSpan.FromSeconds(1);
    var maxBackoff = TimeSpan.FromMinutes(1);
    while (true)
    {
        try
        {
            await ConnectOnceAsync(cfg);
            backoff = TimeSpan.FromSeconds(1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[{DateTimeOffset.UtcNow:u}] channel error: {ex.Message}");
        }

        Console.Error.WriteLine($"reconnecting in {backoff.TotalSeconds}s…");
        await Task.Delay(backoff);
        backoff = TimeSpan.FromTicks(
            Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
    }
}

static async Task ConnectOnceAsync(OutpostConfig cfg)
{
    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Authorization", $"Bearer {cfg.ChannelKey}");
    if (cfg.CertPath is not null && cfg.KeyPath is not null)
    {
        var cert = System.Security.Cryptography.X509Certificates
            .X509Certificate2.CreateFromPemFile(cfg.CertPath, cfg.KeyPath);
        ws.Options.ClientCertificates.Add(cert);
    }

    var connectUri = new Uri(cfg.ConnectUrl);
    await ws.ConnectAsync(connectUri, CancellationToken.None);
    Console.WriteLine($"[{DateTimeOffset.UtcNow:u}] connected → {connectUri}");

    var running = new System.Collections.Concurrent
        .ConcurrentDictionary<string, CancellationTokenSource>();
    var draining = false;

    await SendAsync(ws, OutpostFrames.Create(
        OutpostFrameTypes.Hello, null,
        new OutpostHelloPayload(
            ThisAssemblyVersion(), Math.Max(1, Environment.ProcessorCount),
            null, null)).Signed(cfg.ChannelKey), CancellationToken.None);

    using var heartbeatCts = new CancellationTokenSource();
    var heartbeat = Task.Run(async () =>
    {
        while (!heartbeatCts.IsCancellationRequested
            && ws.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(cfg.HeartbeatSec),
                heartbeatCts.Token).SuppressCancel();
            await SendAsync(ws, OutpostFrames.Create(
                OutpostFrameTypes.Heartbeat, null,
                new OutpostHeartbeatPayload(
                    running.Keys.ToList(),
                    Math.Max(0, Environment.ProcessorCount - running.Count)))
                .Signed(cfg.ChannelKey), CancellationToken.None)
                .SuppressCancel();
        }
    });

    var buffer = new byte[1 << 20];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close) break;

            var frame = OutpostFrame.Parse(ms.ToArray());
            if (frame is null || !frame.Verify(cfg.ChannelKey))
            {
                Console.Error.WriteLine("dropping unsigned/bad frame");
                continue;
            }

            switch (frame.Type)
            {
                case OutpostFrameTypes.Drain:
                    draining = true;
                    await SendAsync(ws, new OutpostFrame
                    {
                        Type = OutpostFrameTypes.Ack,
                        TaskId = frame.TaskId,
                    }.Signed(cfg.ChannelKey), CancellationToken.None);
                    break;
                case OutpostFrameTypes.Kill:
                    if (frame.TaskId is { } tid
                        && running.TryRemove(tid, out var cts))
                    {
                        cts.Cancel();
                    }
                    await SendAsync(ws, new OutpostFrame
                    {
                        Type = OutpostFrameTypes.Ack,
                        TaskId = frame.TaskId,
                    }.Signed(cfg.ChannelKey), CancellationToken.None);
                    break;
                case OutpostFrameTypes.TaskAssign when !draining:
                    var payload = frame.ReadPayload<OutpostTaskPayload>();
                    if (payload is not null && frame.TaskId is { } taskId)
                    {
                        var tcts = CancellationTokenSource
                            .CreateLinkedTokenSource(heartbeatCts.Token);
                        running[taskId] = tcts;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await RunTaskAsync(
                                    ws, cfg, taskId, payload, tcts);
                            }
                            finally
                            {
                                running.TryRemove(taskId, out _);
                            }
                        });
                    }
                    break;
            }
        }
    }
    finally
    {
        heartbeatCts.Cancel();
        foreach (var (_, c) in running) c.Cancel();
        try { await heartbeat; } catch { /* shutdown */ }
    }
}

static async Task RunTaskAsync(
    ClientWebSocket ws, OutpostConfig cfg, string taskId,
    OutpostTaskPayload task, CancellationTokenSource cts)
{
    try
    {
        var evt = await ExecuteAsync(task, cts.Token);
        await SendAsync(ws, OutpostFrames.Create(
            OutpostFrameTypes.TaskEvent, taskId, evt)
            .Signed(cfg.ChannelKey), CancellationToken.None);
    }
    catch (OperationCanceledException)
    {
        await SendAsync(ws, OutpostFrames.Create(
            OutpostFrameTypes.TaskEvent, taskId,
            new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Failed,
                Error = "killed",
            }).Signed(cfg.ChannelKey), CancellationToken.None);
    }
    catch (Exception ex)
    {
        await SendAsync(ws, OutpostFrames.Create(
            OutpostFrameTypes.TaskEvent, taskId,
            new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Failed,
                Error = ex.Message,
            }).Signed(cfg.ChannelKey), CancellationToken.None);
    }
}

// ---------------- frame executor (Docker.DotNet) ----------------

static async Task<OutpostTaskEvent> ExecuteAsync(
    OutpostTaskPayload t, CancellationToken ct)
{
    using var docker = NewDocker();
    var timeout = t.TimeoutMs is > 0
        ? TimeSpan.FromMilliseconds(t.TimeoutMs.Value)
        : TimeSpan.FromMinutes(5);
    using var timeoutCts = CancellationTokenSource
        .CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(timeout);
    var tok = timeoutCts.Token;

    switch (t.Op)
    {
        case OutpostTaskOps.Provision when t.Image is not null:
            {
                var env = (t.Env ?? []).ToList();
                foreach (var s in t.SecretNames ?? [])
                {
                    // SPEC-153 hard rule: only NAMES cross the channel — the
                    // outpost resolves values from its own environment.
                    if (Environment.GetEnvironmentVariable(
                        $"SC_OUTPOST_SECRET_{s}") is { } v)
                    {
                        env.Add($"{s}={v}");
                    }
                }

                var p = new CreateContainerParameters
                {
                    Name = t.Name ?? "",
                    Image = t.Image,
                    Env = env,
                    Cmd = t.Cmd?.ToList() ?? [],
                    WorkingDir = t.WorkingDir ?? "",
                    Labels = t.Labels?.ToDictionary(k => k.Key, v => v.Value)
                        ?? [],
                    HostConfig = new HostConfig
                    {
                        Binds = t.Binds?.ToList() ?? [],
                        Memory = t.MemoryBytes ?? 0,
                        NanoCPUs = t.NanoCpus ?? 0,
                        PidsLimit = t.PidsLimit,
                    },
                };
                await PullImageAsync(docker, t.Image!, tok);
                var created = await docker.Containers
                    .CreateContainerAsync(p, tok);
                await docker.Containers.StartContainerAsync(created.ID, null, tok);
                var data = JsonSerializer.SerializeToElement(
                    new { container_id = created.ID },
                    OutpostFrame.JsonOptions);
                return new OutpostTaskEvent
                {
                    Status = OutpostTaskStatus.Completed,
                    Data = data,
                };
            }
        case OutpostTaskOps.Exec:
            {
                var ep = new ContainerExecCreateParameters
                {
                    Cmd = ["/bin/sh", "-c", t.Command ?? "true"],
                    AttachStdout = true,
                    AttachStderr = true,
                };
                var exec = await docker.Exec
                    .CreateContainerExecAsync(t.ContainerId!, ep, tok);
                using var stream = await docker.Exec
                    .StartContainerExecAsync(exec.ID,
                        new ContainerExecStartParameters { TTY = false }, tok);
                var (stdout, stderr) = await ReadMuxAsync(stream, tok);
                var inspect = await docker.Exec.InspectContainerExecAsync(exec.ID, tok);
                return new OutpostTaskEvent
                {
                    Status = inspect.ExitCode == 0
                        ? OutpostTaskStatus.Completed
                        : OutpostTaskStatus.Failed,
                    ExitCode = (int?)inspect.ExitCode,
                    StdOut = stdout,
                    StdErr = stderr,
                    Error = inspect.ExitCode == 0 ? null : $"exit {inspect.ExitCode}",
                };
            }
        case OutpostTaskOps.ReadFile:
            {
                var ep = new ContainerExecCreateParameters
                {
                    Cmd = ["/bin/sh", "-c", $"base64 '{t.Path}'"],
                    AttachStdout = true,
                };
                var exec = await docker.Exec
                    .CreateContainerExecAsync(t.ContainerId!, ep, tok);
                using var stream = await docker.Exec
                    .StartContainerExecAsync(exec.ID,
                        new ContainerExecStartParameters { TTY = false }, tok);
                var (b64, _) = await ReadMuxAsync(stream, tok);
                var data = JsonSerializer.SerializeToElement(
                    new { content_b64 = b64.Trim() },
                    OutpostFrame.JsonOptions);
                return new OutpostTaskEvent
                {
                    Status = OutpostTaskStatus.Completed,
                    Data = data,
                };
            }
        case OutpostTaskOps.WriteFile:
            {
                var b64 = t.ContentB64
                    ?? Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(t.Content ?? ""));
                var ep = new ContainerExecCreateParameters
                {
                    Cmd = ["/bin/sh", "-c",
                    $"echo '{b64}' | base64 -d > '{t.Path}'"],
                    AttachStdout = true,
                    AttachStderr = true,
                };
                var exec = await docker.Exec
                    .CreateContainerExecAsync(t.ContainerId!, ep, tok);
                using var stream = await docker.Exec
                    .StartContainerExecAsync(exec.ID,
                        new ContainerExecStartParameters { TTY = false }, tok);
                var (_, werr) = await ReadMuxAsync(stream, tok);
                var inspect = await docker.Exec
                    .InspectContainerExecAsync(exec.ID, tok);
                return new OutpostTaskEvent
                {
                    Status = inspect.ExitCode == 0
                        ? OutpostTaskStatus.Completed
                        : OutpostTaskStatus.Failed,
                    Error = inspect.ExitCode == 0 ? null : werr.Trim(),
                };
            }
        case OutpostTaskOps.Destroy:
            await docker.Containers.RemoveContainerAsync(
                t.ContainerId!, new ContainerRemoveParameters
                { Force = true, RemoveVolumes = true }, tok);
            return new OutpostTaskEvent
            { Status = OutpostTaskStatus.Completed };
        case OutpostTaskOps.Inspect:
            {
                var info = await docker.Containers
                    .InspectContainerAsync(t.ContainerId!, tok);
                var data = JsonSerializer.SerializeToElement(new
                {
                    container_id = info.ID,
                    state = info.State?.Status,
                }, OutpostFrame.JsonOptions);
                return new OutpostTaskEvent
                {
                    Status = OutpostTaskStatus.Completed,
                    Data = data,
                };
            }
        case OutpostTaskOps.Pause:
            await docker.Containers.PauseContainerAsync(t.ContainerId!, tok);
            return new OutpostTaskEvent
            { Status = OutpostTaskStatus.Completed };
        case OutpostTaskOps.Unpause:
            await docker.Containers.UnpauseContainerAsync(t.ContainerId!, tok);
            return new OutpostTaskEvent
            { Status = OutpostTaskStatus.Completed };
        case OutpostTaskOps.Start:
            await docker.Containers.StartContainerAsync(t.ContainerId!, null, tok);
            return new OutpostTaskEvent
            { Status = OutpostTaskStatus.Completed };
        case OutpostTaskOps.Stop:
            await docker.Containers.StopContainerAsync(
                t.ContainerId!, new ContainerStopParameters(), tok);
            return new OutpostTaskEvent
            { Status = OutpostTaskStatus.Completed };
        default:
            return new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Failed,
                Error = $"unknown op {t.Op}",
            };
    }
}

static async Task PullImageAsync(
    IDockerClient docker, string image, CancellationToken ct)
{
    var parts = image.Split(':', 2);
    await docker.Images.CreateImageAsync(new ImagesCreateParameters
    {
        FromImage = parts[0],
        Tag = parts.Length > 1 ? parts[1] : "latest",
    }, new AuthConfig(), new Progress<JSONMessage>(), ct);
}

static async Task<(string stdout, string stderr)> ReadMuxAsync(
    MultiplexedStream stream, CancellationToken ct)
{
    var so = new StringBuilder();
    var se = new StringBuilder();
    var buf = new byte[8192];
    while (true)
    {
        var r = await stream.ReadOutputAsync(buf, 0, buf.Length, ct);
        if (r.EOF) break;
        (r.Target == MultiplexedStream.TargetStream.StandardError ? se : so)
            .Append(Encoding.UTF8.GetString(buf, 0, r.Count));
    }

    return (so.ToString(), se.ToString());
}

static Task SendAsync(
    ClientWebSocket ws, OutpostFrame frame, CancellationToken ct) =>
    ws.SendAsync(frame.Serialize(), WebSocketMessageType.Text, true, ct);

static IDockerClient NewDocker() =>
    new DockerClientBuilder().WithEndpoint(new Uri(
        Environment.GetEnvironmentVariable("DOCKER_HOST")
        ?? "unix:///var/run/docker.sock")).Build();

static string ConfigDir() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".sandcastle-outpost");

static void Chmod600(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }
}

static string ConfigPath() =>
    Path.Combine(ConfigDir(), "config.json");

static string? ThisAssemblyVersion() =>
    typeof(Program).Assembly.GetName().Version?.ToString();

namespace Sandcastle.Outpost
{
    internal static class JsonOpts
    {
        public static readonly JsonSerializerOptions CfgJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
    }

    internal sealed class OutpostConfig
    {
        [JsonPropertyName("outpost_id")] public Guid OutpostId { get; set; }
        [JsonPropertyName("channel_key")] public string ChannelKey { get; set; } = "";
        [JsonPropertyName("connect_url")] public string ConnectUrl { get; set; } = "";
        [JsonPropertyName("heartbeat_sec")] public int HeartbeatSec { get; set; } = 30;
        [JsonPropertyName("cert_path")] public string? CertPath { get; set; }
        [JsonPropertyName("key_path")] public string? KeyPath { get; set; }
    }

    internal sealed class RegisterResponse
    {
        [JsonPropertyName("outpost_id")] public Guid OutpostId { get; set; }
        [JsonPropertyName("channel_key")] public string ChannelKey { get; set; } = "";
        [JsonPropertyName("heartbeat_sec")] public int HeartbeatSec { get; set; }
        [JsonPropertyName("cert_pem")] public string? CertPem { get; set; }
        [JsonPropertyName("key_pem")] public string? KeyPem { get; set; }
    }

    internal static class TaskExt
    {
        public static async Task SuppressCancel(this Task t)
        {
            try { await t; }
            catch (OperationCanceledException) { }
        }
    }
}