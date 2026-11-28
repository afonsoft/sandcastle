using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.Tools;

using Shouldly;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-192: rich document readers — generated fixtures, limits, provenance and safety.</summary>
public sealed class Spec192DocumentToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sc192-" + Guid.NewGuid().ToString("N"));

    public Spec192DocumentToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Registry_exposes_readers_as_read_only() // RF-006
    {
        var services = new ServiceCollection()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .AddSingleton<IToolRegistry, ToolRegistry>()
            .BuildServiceProvider();
        var registry = services.GetRequiredService<IToolRegistry>();
        registry.Find("read_pdf")!.Risky.ShouldBeFalse();
        registry.Find("read_archive")!.Risky.ShouldBeFalse();
        registry.Find("read_sqlite")!.Risky.ShouldBeFalse();
    }

    [Fact]
    public async Task Invalid_workspace_path_is_rejected_before_execution() // RF-005
    {
        var executor = new FakeSandboxExecutor();
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = new ToolCallContext(Guid.NewGuid(),
            new Sandbox(Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
                null, DateTimeOffset.UtcNow, null, null),
            executor, new FakeEventService(), services, TimeSpan.FromSeconds(10));
        var result = await BuiltinDocumentTools.ReadArchive().Handler(ctx,
            JsonSerializer.SerializeToElement(new { path = "../secret.zip" }), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.Observation.GetProperty("error").GetString().ShouldBe("workspace_path_invalid");
        executor.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void Archive_script_lists_and_reads_generated_zip_with_provenance() // RF-002/RF-004
    {
        File.WriteAllText(Path.Combine(_dir, "hello.txt"), "hello archive");
        RunPythonInline("""
import zipfile,os
with zipfile.ZipFile('fixture.zip','w') as z:
 z.write('hello.txt')
 z.writestr('bin.dat',b'a\0b')
""");
        var list = RunReader("ArchiveScript", "fixture.zip", "list", "", "10", "2000");
        list.GetProperty("archive_kind").GetString().ShouldBe("zip");
        list.GetProperty("sha256").GetString().ShouldNotBeNullOrWhiteSpace();
        list.GetProperty("entries_returned").GetInt32().ShouldBe(2);
        var read = RunReader("ArchiveScript", "fixture.zip", "read", "hello.txt", "10", "2000");
        read.GetProperty("text").GetString().ShouldBe("hello archive");
        read.GetProperty("binary").GetBoolean().ShouldBeFalse();
        var bin = RunReader("ArchiveScript", "fixture.zip", "read", "bin.dat", "10", "2000");
        bin.GetProperty("binary").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Sqlite_script_reads_schema_and_bounded_select_only() // RF-003/RF-005
    {
        RunPythonInline("""
import sqlite3
c=sqlite3.connect('fixture.db'); c.execute('create table items(id integer primary key,name text)'); c.execute("insert into items(name) values ('a'),('b'),('c')"); c.commit(); c.close()
""");
        var tables = RunReader("SqliteScript", "fixture.db", "tables", "", "10", "4000");
        tables.GetProperty("tables")[0].GetProperty("name").GetString().ShouldBe("items");
        var query = RunReader("SqliteScript", "fixture.db", "query", "select id,name from items order by id", "2", "4000");
        query.GetProperty("rows_returned").GetInt32().ShouldBe(2);
        query.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        var write = RunReaderRaw("SqliteScript", "fixture.db", "query", "delete from items", "2", "4000");
        write.ExitCode.ShouldNotBe(0);
        write.StdErr.ShouldContain("read_only_select_required");
    }

    [Fact]
    public void Pdf_script_extracts_simple_generated_pdf_with_page_marker() // RF-001/RF-004
    {
        File.WriteAllBytes(Path.Combine(_dir, "fixture.pdf"), SimplePdf("Hello PDF reader"));
        var result = RunReader("PdfScript", "fixture.pdf", "5", "4000");
        result.GetProperty("extractor").GetString().ShouldBe("stdlib-simple");
        result.GetProperty("text").GetString()!.ShouldContain("[page 1]");
        result.GetProperty("text").GetString()!.ShouldContain("Hello PDF reader");
        result.GetProperty("sha256").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Pdf_script_reports_encrypted_files_clearly() // RF-001 error path
    {
        File.WriteAllBytes(Path.Combine(_dir, "encrypted.pdf"), SimplePdf("x", encryptedMarker: true));
        var result = RunReaderRaw("PdfScript", "encrypted.pdf", "5", "4000");
        result.ExitCode.ShouldNotBe(0);
        result.StdErr.ShouldContain("encrypted_pdf");
    }

    private JsonElement RunReader(string field, params string[] args)
    {
        var raw = RunReaderRaw(field, args);
        raw.StdErr.ShouldBe("", $"reader failed: {raw.StdErr}");
        raw.ExitCode.ShouldBe(0);
        return JsonDocument.Parse(raw.StdOut).RootElement.Clone();
    }

    private (int ExitCode, string StdOut, string StdErr) RunReaderRaw(string field, params string[] args)
    {
        var script = typeof(BuiltinDocumentTools).GetField(field,
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as string;
        var psi = new ProcessStartInfo("python3")
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        process.WaitForExit(20_000);
        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private void RunPythonInline(string code)
    {
        var psi = new ProcessStartInfo("python3", "-")
        { WorkingDirectory = _dir, RedirectStandardInput = true };
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(code);
        process.StandardInput.Close();
        process.WaitForExit(10_000).ShouldBeTrue();
    }

    private static byte[] SimplePdf(string text, bool encryptedMarker = false)
    {
        var content = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var pdf = "%PDF-1.4\n" + (encryptedMarker ? "1 0 obj << /Encrypt true >> endobj\n" : "")
            + $"2 0 obj << /Length {content.Length} >> stream\n{content}\nendstream endobj\n%%EOF\n";
        return System.Text.Encoding.Latin1.GetBytes(pdf);
    }
}