using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>
/// SPEC-163 RF-001/RF-004: stats + saúde por automation e idempotência
/// de webhook (dedup 24h por Idempotency-Key ou hash do payload).
/// </summary>
public sealed class Spec163AutomationObservabilityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec163-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec163AutomationObservabilityTests()
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

    private async Task<HttpClient> LoginAsync(string email)
    {
        var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email, displayName = email.Split('@')[0] });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Stats_Roundtrip_E_404_Para_Inexistente()
    {
        var http = await LoginAsync("alice@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var cdoc = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var id = cdoc.RootElement.GetProperty("id").GetString();

        var res = await http.GetAsync($"/api/v1/automations/{id}/stats");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("total").GetInt64().ShouldBe(0);
        doc.RootElement.GetProperty("health").GetString()
            .ShouldBe("green");
        doc.RootElement.GetProperty("last_runs").GetArrayLength()
            .ShouldBe(0);

        var missing = await http.GetAsync(
            $"/api/v1/automations/{Guid.NewGuid()}/stats");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lista_Expoe_Campo_Health()
    {
        var http = await LoginAsync("alice@example.com");
        await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        var res = await http.GetAsync("/api/v1/automations");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        doc.RootElement[0].GetProperty("health").GetString()
            .ShouldBe("green");
    }

    [Fact]
    public async Task Hook_Mesma_Idempotency_Key_Deduplica()
    {
        var http = await LoginAsync("alice@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        using var cdoc = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var id = cdoc.RootElement.GetProperty("id").GetString();
        var hook = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null);
        hook.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var hdoc = JsonDocument.Parse(
            await hook.Content.ReadAsStringAsync());
        var token = hdoc.RootElement.GetProperty("token").GetString();

        var req1 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/hooks/{token}");
        req1.Headers.Add("Idempotency-Key", "key-1");
        req1.Content = new StringContent("{}", System.Text.Encoding.UTF8,
            "application/json");
        var first = await http.SendAsync(req1);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var req2 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/hooks/{token}");
        req2.Headers.Add("Idempotency-Key", "key-1");
        req2.Content = new StringContent("payload-diferente",
            System.Text.Encoding.UTF8, "application/json");
        var second = await http.SendAsync(req2);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var sdoc = JsonDocument.Parse(
            await second.Content.ReadAsStringAsync());
        sdoc.RootElement.GetProperty("deduplicated").GetBoolean()
            .ShouldBeTrue();

        // chave diferente → novo run (202).
        var req3 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/hooks/{token}");
        req3.Headers.Add("Idempotency-Key", "key-2");
        req3.Content = new StringContent("{}", System.Text.Encoding.UTF8,
            "application/json");
        var third = await http.SendAsync(req3);
        third.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Hook_Sem_Header_Deduplica_Pelo_Hash_Do_Body()
    {
        var http = await LoginAsync("alice@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        using var cdoc = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var id = cdoc.RootElement.GetProperty("id").GetString();
        var hook = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null);
        using var hdoc = JsonDocument.Parse(
            await hook.Content.ReadAsStringAsync());
        var token = hdoc.RootElement.GetProperty("token").GetString();

        for (var i = 0; i < 2; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/hooks/{token}");
            req.Content = new StringContent("{\"a\":1}",
                System.Text.Encoding.UTF8, "application/json");
            var res = await http.SendAsync(req);
            if (i == 0)
            {
                res.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            }
            else
            {
                res.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var doc = JsonDocument.Parse(
                    await res.Content.ReadAsStringAsync());
                doc.RootElement.GetProperty("deduplicated").GetBoolean()
                    .ShouldBeTrue();
            }
        }
    }

    [Fact]
    public async Task Hook_Retry_Concorrente_Deduplica_Uma_Vez()
    {
        var http = await LoginAsync("alice@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        using var cdoc = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var id = cdoc.RootElement.GetProperty("id").GetString();
        var hook = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null);
        using var hdoc = JsonDocument.Parse(
            await hook.Content.ReadAsStringAsync());
        var token = hdoc.RootElement.GetProperty("token").GetString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/v1/hooks/{token}");
                req.Headers.Add("Idempotency-Key", "concurrent-key");
                req.Content = new StringContent("{}",
                    System.Text.Encoding.UTF8, "application/json");
                return http.SendAsync(req);
            }));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted)
            .ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK)
            .ShouldBe(1);

        var stats = await http.GetAsync($"/api/v1/automations/{id}/stats");
        using var sdoc = JsonDocument.Parse(
            await stats.Content.ReadAsStringAsync());
        sdoc.RootElement.GetProperty("total").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Hook_Payload_Acima_Do_Limite_Retorna_413()
    {
        var http = await LoginAsync("alice@example.com");
        var created = await http.PostAsJsonAsync("/api/v1/automations", new
        {
            name = "auto",
            prompt = "run",
            schedule_kind = "interval",
            interval_minutes = 30,
        });
        using var cdoc = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var id = cdoc.RootElement.GetProperty("id").GetString();
        var hook = await http.PostAsync(
            $"/api/v1/automations/{id}/hooks", null);
        using var hdoc = JsonDocument.Parse(
            await hook.Content.ReadAsStringAsync());
        var token = hdoc.RootElement.GetProperty("token").GetString();

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/hooks/{token}")
        {
            Content = new ByteArrayContent(new byte[(64 * 1024) + 1]),
        };
        var res = await http.SendAsync(req);
        res.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}