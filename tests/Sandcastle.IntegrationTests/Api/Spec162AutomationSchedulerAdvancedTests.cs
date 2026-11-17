using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-162 RF-001/RF-004/RF-007: contrato HTTP dos campos avançados —
/// kind=cron + expressão + timezone IANA + approval preset; 400 em
/// expressão cron inválida e preset fora do conjunto fechado.
/// </summary>
public sealed class Spec162AutomationSchedulerAdvancedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec162-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec162AutomationSchedulerAdvancedTests()
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

    private async Task<HttpClient> LoginAsync()
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "u@example.com", displayName = "u" });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Create_Cron_Roundtrip_Com_Timezone_E_Preset()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "cron job",
            prompt = "run",
            schedule_kind = "cron",
            cron_expression = "0 9 * * 1-5",
            time_zone_id = "America/Sao_Paulo",
            approval_preset = "allow-readonly",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("schedule_kind").GetString()
            .ShouldBe("cron");
        doc.RootElement.GetProperty("cron_expression").GetString()
            .ShouldBe("0 9 * * 1-5");
        doc.RootElement.GetProperty("time_zone_id").GetString()
            .ShouldBe("America/Sao_Paulo");
        doc.RootElement.GetProperty("approval_preset").GetString()
            .ShouldBe("allow-readonly");
        doc.RootElement.GetProperty("next_run_at").ValueKind.ShouldBe(
            JsonValueKind.String);
    }

    [Fact]
    public async Task Create_Cron_Sem_Expressao_Retorna_400()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "cron job",
            prompt = "run",
            schedule_kind = "cron",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Cron_Expressao_Invalida_Retorna_400()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "cron job",
            prompt = "run",
            schedule_kind = "cron",
            cron_expression = "não é cron",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ApprovalPreset_Invalido_Retorna_400()
    {
        var http = await LoginAsync();
        var res = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
            approval_preset = "yolo",
        });
        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Schedule_Projeta_Next_Runs_Com_Cron_E_Timezone()
    {
        var http = await LoginAsync();
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "cron job",
            prompt = "run",
            schedule_kind = "cron",
            cron_expression = "0 9 * * *",
            time_zone_id = "UTC",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var now = DateTimeOffset.UtcNow;
        var res = await http.GetAsync(
            $"/api/v1/automations/schedule?from={Uri.EscapeDataString(now.UtcDateTime.ToString("O"))}"
            + $"&to={Uri.EscapeDataString(now.AddDays(7).UtcDateTime.ToString("O"))}");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        doc.RootElement.GetArrayLength().ShouldBe(1);
        var entry = doc.RootElement[0];
        entry.GetProperty("next_runs").GetArrayLength().ShouldBeGreaterThan(0);
    }
}