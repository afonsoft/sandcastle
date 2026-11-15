using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandcastle.Application.Outpost;

/// <summary>
/// SPEC-153 RF-002: <c>outpost_frame_v1</c> — wire protocol between the
/// control plane and a <c>sandcastle-outpost</c> runner over a single
/// outbound-only WebSocket. Frames are JSON (snake_case) and HMAC-SHA256
/// signed with the session channel key (<c>opk-*</c>) issued at register:
/// the plane validates outpost frames and vice-versa (tamper-evident
/// channel on top of TLS).
/// </summary>
public static class OutpostFrameTypes
{
    /// <summary>Outpost → plane, first frame: version/labels/max_sandboxes.</summary>
    public const string Hello = "hello";

    /// <summary>Outpost → plane liveness tick (carries running task ids).</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>Plane → outpost: sandbox op assignment (provision/exec/…).</summary>
    public const string TaskAssign = "task_assign";

    /// <summary>Outpost → plane: task progress/result (started|output|completed|failed).</summary>
    public const string TaskEvent = "task_event";

    /// <summary>Plane → outpost: abort a running task (revoke/kill).</summary>
    public const string Kill = "kill";

    /// <summary>Plane → outpost: stop accepting tasks, finish in-flight.</summary>
    public const string Drain = "drain";

    /// <summary>Acknowledgement (either direction) for hello/kill/drain.</summary>
    public const string Ack = "ack";
}

/// <summary>Sandbox ops carried by <c>task_assign</c> frames (v1 scope).</summary>
public static class OutpostTaskOps
{
    /// <summary>Pull/create/start a sandbox container on the outpost docker.</summary>
    public const string Provision = "provision";

    /// <summary>Run a command in the container; output streams back.</summary>
    public const string Exec = "exec";

    /// <summary>Read a file from the container workspace.</summary>
    public const string ReadFile = "read_file";

    /// <summary>Write a file in the container workspace.</summary>
    public const string WriteFile = "write_file";

    /// <summary>Destroy the sandbox container (idempotent).</summary>
    public const string Destroy = "destroy";

    /// <summary>Inspect container state.</summary>
    public const string Inspect = "inspect";

    /// <summary>docker pause (memory-preserving).</summary>
    public const string Pause = "pause";

    /// <summary>docker unpause.</summary>
    public const string Unpause = "unpause";

    /// <summary>docker start (stopped container).</summary>
    public const string Start = "start";

    /// <summary>docker stop.</summary>
    public const string Stop = "stop";
}

/// <summary>One <c>outpost_frame_v1</c> envelope.</summary>
public sealed record OutpostFrame
{
    /// <summary>Frame type — see <see cref="OutpostFrameTypes"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Correlation id for task_assign/task_event/kill/ack.</summary>
    [JsonPropertyName("task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }

    /// <summary>Type-specific payload (raw JSON).</summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// HMAC-SHA256 (hex) of <c>type\n task_id\n payload</c> canonical JSON,
    /// keyed by the session channel key. Null only on unsigned transports
    /// (dev loopback) — both peers verify when a key is known.
    /// </summary>
    [JsonPropertyName("sig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sig { get; init; }

    /// <summary>Shared serializer options — snake_case, explicit fields.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes the frame (unsigned — see <see cref="Signed"/>).</summary>
    public byte[] Serialize() =>
        JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>Parses a frame; null on malformed input (never throws).</summary>
    public static OutpostFrame? Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var f = JsonSerializer.Deserialize<OutpostFrame>(utf8, JsonOptions);
            return f is { Type.Length: > 0 } ? f : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a signed copy: <c>sig = HMAC_SHA256(key, canonical)</c> where
    /// canonical is <c>type + "\n" + task_id + "\n" + payload_raw</c>.
    /// </summary>
    public OutpostFrame Signed(string channelKey)
    {
        var canonical = CanonicalBytes();
        var sig = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(channelKey), canonical));
        return this with { Sig = sig };
    }

    /// <summary>Verifies <see cref="Sig"/> against the channel key.</summary>
    public bool Verify(string channelKey)
    {
        if (Sig is not { Length: 64 } sig)
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(channelKey), CanonicalBytes()));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(sig));
    }

    /// <summary>
    /// Canonical signing input — payload serialized compactly so peers
    /// agree regardless of whitespace (payload is already a JsonElement
    /// round-tripped through the same options, making this stable).
    /// </summary>
    private byte[] CanonicalBytes()
    {
        var sb = new StringBuilder();
        sb.Append(Type).Append('\n').Append(TaskId).Append('\n');
        if (Payload is { } p)
        {
            sb.Append(p.GetRawText());
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>Convenience constructors for typed frames.</summary>
public static class OutpostFrames
{
    /// <summary>Serialize <paramref name="payload"/> into a frame.</summary>
    public static OutpostFrame Create<T>(string type, string? taskId, T payload) =>
        new()
        {
            Type = type,
            TaskId = taskId,
            Payload = JsonSerializer.SerializeToElement(payload,
                OutpostFrame.JsonOptions),
        };

    /// <summary>Reads <see cref="OutpostFrame.Payload"/> as <typeparamref name="T"/>.</summary>
    public static T? ReadPayload<T>(this OutpostFrame frame) =>
        frame.Payload is { } p
            ? p.Deserialize<T>(OutpostFrame.JsonOptions)
            : default;
}

/// <summary><c>hello</c> payload — capabilities reported at connect.</summary>
public sealed record OutpostHelloPayload(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("max_sandboxes")] int MaxSandboxes,
    [property: JsonPropertyName("labels")]
    IReadOnlyDictionary<string, string>? Labels,
    [property: JsonPropertyName("name")] string? Name);

/// <summary><c>heartbeat</c> payload — liveness + running task ids.</summary>
public sealed record OutpostHeartbeatPayload(
    [property: JsonPropertyName("running_tasks")] IReadOnlyList<string>? RunningTasks,
    [property: JsonPropertyName("free_slots")] int FreeSlots);

/// <summary>
/// <c>task_assign</c> payload — one sandbox op. Secret VALUES are never
/// carried (SPEC-153 RF-003 hard rule): <see cref="SecretNames"/> lists
/// names the outpost resolves from its own vault/environment.
/// </summary>
public sealed class OutpostTaskPayload
{
    /// <summary>Op — see <see cref="OutpostTaskOps"/>.</summary>
    [JsonPropertyName("op")]
    public required string Op { get; set; }

    // ---- provision ----
    /// <summary>Container name (provision).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Image to pull/run (provision).</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>Container env (<c>NAME=value</c>) — secrets excluded.</summary>
    [JsonPropertyName("env")]
    public IReadOnlyList<string>? Env { get; set; }

    /// <summary>Bind mounts <c>host:container</c> (provision).</summary>
    [JsonPropertyName("binds")]
    public IReadOnlyList<string>? Binds { get; set; }

    /// <summary>Container labels (provision).</summary>
    [JsonPropertyName("labels")]
    public IReadOnlyDictionary<string, string>? Labels { get; set; }

    /// <summary>Memory limit bytes (provision).</summary>
    [JsonPropertyName("memory_bytes")]
    public long? MemoryBytes { get; set; }

    /// <summary>CPU quota (nano cpus) (provision).</summary>
    [JsonPropertyName("nano_cpus")]
    public long? NanoCpus { get; set; }

    /// <summary>PID limit (provision).</summary>
    [JsonPropertyName("pids_limit")]
    public long? PidsLimit { get; set; }

    /// <summary>Container command override (provision).</summary>
    [JsonPropertyName("cmd")]
    public IReadOnlyList<string>? Cmd { get; set; }

    /// <summary>Working dir (provision).</summary>
    [JsonPropertyName("working_dir")]
    public string? WorkingDir { get; set; }

    /// <summary>
    /// Secret NAMES the outpost resolves into env — names only; values
    /// live in <c>OUTPOST_SECRETS_FILE</c>/env on the outpost side and
    /// never cross the channel.
    /// </summary>
    [JsonPropertyName("secret_names")]
    public IReadOnlyList<string>? SecretNames { get; set; }

    // ---- exec / file ops ----
    /// <summary>Target container id (exec/read/write/destroy/…).</summary>
    [JsonPropertyName("container_id")]
    public string? ContainerId { get; set; }

    /// <summary>Shell command (exec).</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Per-op timeout hint in ms (exec/provision).</summary>
    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; set; }

    /// <summary>File path (read_file/write_file).</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Text content (write_file).</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Binary-safe content (write_file — preferred over text).</summary>
    [JsonPropertyName("content_b64")]
    public string? ContentB64 { get; set; }
}

/// <summary><c>task_event</c> payload status values.</summary>
public static class OutpostTaskStatus
{
    /// <summary>Task accepted/started.</summary>
    public const string Started = "started";

    /// <summary>Incremental output chunk (stdout/stderr).</summary>
    public const string Output = "output";

    /// <summary>Terminal success — <c>data</c> carries the op result.</summary>
    public const string Completed = "completed";

    /// <summary>Terminal failure — <c>error</c> populated.</summary>
    public const string Failed = "failed";
}

/// <summary><c>task_event</c> payload — progress or terminal result.</summary>
public sealed class OutpostTaskEvent
{
    /// <summary>Status — see <see cref="OutpostTaskStatus"/>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    /// <summary>Output channel for <c>output</c> events (stdout|stderr).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>Output text for <c>output</c> events (already redacted).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Exit code for terminal events (exec/provision).</summary>
    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }

    /// <summary>Aggregated stdout (terminal exec events).</summary>
    [JsonPropertyName("stdout")]
    public string? StdOut { get; set; }

    /// <summary>Aggregated stderr (terminal exec events).</summary>
    [JsonPropertyName("stderr")]
    public string? StdErr { get; set; }

    /// <summary>Error detail on <c>failed</c> events.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Structured op result (inspect/read_file/etc.).</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}
