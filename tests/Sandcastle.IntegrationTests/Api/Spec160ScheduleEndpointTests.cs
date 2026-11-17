using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-160 RF-001/RF-004: <c>GET /api/v1/automations/schedule</c> —
/// projeção next_runs + runs reais por automação; 400 em janela
/// inválida/>62d; auth obrigatória; disabled ausente.
/// </summary>
public sealed class Spec160ScheduleEndpointTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec160-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec160ScheduleEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "Sqlite");
                b.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                b.UseSetting("Observability:OtlpEndpoint", string.Empty);
                b.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                b.UseSetting("Auth:Provider", "none");
                b.UseSetting("Auth:UserProvisioningEnabled", "true");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
    }

    private async Task<HttpClient> LoginUserAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    private static string Iso(DateTimeOffset d) =>
        Uri.EscapeDataString(d.UtcDateTime.ToString("O"));

    [Fact]
    public async Task Schedule_Projeta_NextRuns_Por_Automation()
    {
        var http = await LoginUserAsync("cal@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations",
            new
            {
                name = "hourly",
                prompt = "p",
                schedule_kind = "interval",
                interval_minutes = 120,
            });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(1);
        var data = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/automations/schedule?from={Iso(from)}&to={Iso(to)}");
        var entry = data.EnumerateArray().Single(
            e => e.GetProperty("automation_id").GetGuid() == id);
        entry.GetProperty("name").GetString().ShouldBe("hourly");
        var next = entry.GetProperty("next_runs");
        next.GetArrayLength().ShouldBeGreaterThan(5);
        next.EnumerateArray().All(
            r => r.GetDateTimeOffset() >= from
                && r.GetDateTimeOffset() <= to).ShouldBeTrue();
        entry.GetProperty("runs").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Schedule_Inclui_Runs_Reais_Da_Janela()
    {
        var http = await LoginUserAsync("calruns@example.com");
        var id = (await (await http.PostAsJsonAsync(
                "/api/v1/automations", new
                {
                    name = "once-daily",
                    prompt = "p",
                    schedule_kind = "interval",
                    interval_minutes = 60,
                })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        (await http.PostAsync(
            $"/api/v1/automations/{id}/run-now", null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var from = DateTimeOffset.UtcNow.AddMinutes(-5);
        var to = from.AddHours(2);
        var data = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/automations/schedule?from={Iso(from)}&to={Iso(to)}");
        var entry = data.EnumerateArray().Single(
            e => e.GetProperty("automation_id").GetGuid() == id);
        var run = entry.GetProperty("runs").EnumerateArray().Single();
        run.GetProperty("status").GetString()
            .ShouldBeOneOf("running", "succeeded", "failed", "skipped");
        run.GetProperty("started_at").ValueKind
            .ShouldBe(JsonValueKind.String);
        run.GetProperty("conversation_id").ValueKind
            .ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Schedule_400_Em_Janela_Invalida_E_Disabled_Oculta()
    {
        var http = await LoginUserAsync("calbad@example.com");
        var now = DateTimeOffset.UtcNow;

        (await http.GetAsync("/api/v1/automations/schedule"
            + $"?from={Iso(now)}&to={Iso(now.AddDays(63))}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.GetAsync("/api/v1/automations/schedule"
            + $"?from={Iso(now)}&to={Iso(now.AddDays(-1))}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await http.GetAsync("/api/v1/automations/schedule"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // automation disabled não aparece na agenda
        var created = await http.PostAsJsonAsync("/api/v1/automations",
            new
            {
                name = "off",
                prompt = "p",
                schedule_kind = "interval",
                interval_minutes = 60,
            });
        var id = (await created.Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await http.PatchAsJsonAsync(
            $"/api/v1/automations/{id}", new { enabled = false }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/automations/schedule"
            + $"?from={Iso(now)}&to={Iso(now.AddDays(1))}");
        data.EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Schedule_Anonymous_401()
    {
        var anon = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var now = DateTimeOffset.UtcNow;
        (await anon.GetAsync("/api/v1/automations/schedule"
            + $"?from={Iso(now)}&to={Iso(now.AddDays(1))}"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}