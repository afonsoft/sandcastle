using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Sandcastle.IntegrationTests.Identity;

/// <summary>
/// SPEC-158: account settings — PAT CRUD + bearer auth + escopo,
/// perfil, senha (provider gate), sessões, notificações, export.
/// </summary>
public sealed class Spec158AccountTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-158-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public Spec158AccountTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Database:Provider", "Sqlite");
                builder.UseSetting("Database:ConnectionString",
                    $"Data Source={_dbPath};Default Timeout=10");
                builder.UseSetting("Observability:OtlpEndpoint",
                    string.Empty);
                builder.UseSetting("Sandbox:DefaultImage", "bash:5");
                builder.UseSetting("Sandbox:InstanceOwner",
                    $"test-{Guid.NewGuid():N}");
                builder.UseSetting("Sandbox:WorkspaceRoot",
                    Path.Combine(Path.GetTempPath(),
                        $"sc-158-ws-{Guid.NewGuid():N}"));
                builder.UseSetting("Auth:Provider", "none");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private HttpClient NewClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<HttpClient> LoginAsync(HttpClient http)
    {
        var res = await http.PostAsJsonAsync("/api/v1/auth/dev-login",
            new { email = "acct@x.test", displayName = "acct" });
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        return http;
    }

    [Fact]
    public async Task Pat_Create_List_Validate_Revoke()
    {
        var http = await LoginAsync(NewClient());

        var created = await http.PostAsJsonAsync(
            "/api/v1/users/me/tokens",
            new { name = "ci", scopes = new[] { "read" } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = doc.GetProperty("token").GetString()!;
        token.ShouldStartWith("sc_pat_");
        var id = doc.GetProperty("id").GetGuid();

        // Lista mascara o token.
        var list = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/me/tokens");
        var t = list.GetProperty("tokens").EnumerateArray().Single();
        t.GetProperty("prefix").GetString()!.ShouldStartWith("sc_pat_");
        t.TryGetProperty("token", out _).ShouldBeFalse();

        // PAT autentica em endpoint autenticado.
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new("Bearer", token);
        (await bearer.GetAsync("/api/v1/users/me/preferences"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Escopo read-only bloqueia métodos mutantes.
        (await bearer.PutAsJsonAsync("/api/v1/users/me/preferences",
            new { theme = "dark" })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // Revoga → 401.
        (await http.DeleteAsync($"/api/v1/users/me/tokens/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bearer.GetAsync("/api/v1/users/me/preferences"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pat_Write_Scope_Allows_Mutations()
    {
        var http = await LoginAsync(NewClient());
        var created = await http.PostAsJsonAsync(
            "/api/v1/users/me/tokens",
            new { name = "rw", scopes = new[] { "read", "write" } });
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new("Bearer", doc.GetProperty("token").GetString()!);
        (await bearer.PutAsJsonAsync("/api/v1/users/me/preferences",
            new { theme = "dark" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Profile_Patch_And_Sessions()
    {
        var http = await LoginAsync(NewClient());

        (await http.PatchAsJsonAsync("/api/v1/users/me",
            new
            {
                display_name = "Novo Nome",
                avatar_url = "https://x.test/a.png"
            }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessions = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/me/sessions");
        sessions.GetProperty("sessions").EnumerateArray()
            .Single().GetProperty("current").GetBoolean().ShouldBeTrue();

        (await http.DeleteAsync("/api/v1/users/me/sessions/other"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Sign out por último — invalida o cookie do próprio client.
        (await http.DeleteAsync("/api/v1/users/me/sessions/current"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Password_Gated_By_Provider()
    {
        // Factory roda Auth:Provider=none → 403.
        var http = await LoginAsync(NewClient());
        (await http.PostAsJsonAsync("/api/v1/users/me/password",
            new { current_password = "x", new_password = "fake-new-pass!" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NotificationPrefs_Reject_Insecure_Webhook()
    {
        var http = await LoginAsync(NewClient());
        (await http.PutAsJsonAsync("/api/v1/users/me/notification-prefs",
            new
            {
                run_finished = true,
                run_failed = false,
                webhook_url = "http://insecure.test/hook"
            }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var prefs = await http.GetFromJsonAsync<JsonElement>(
            "/api/v1/users/me/notification-prefs");
        prefs.GetProperty("run_finished").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Export_Returns_Personal_Data_Json()
    {
        var http = await LoginAsync(NewClient());
        var res = await http.GetAsync("/api/v1/users/me/export");
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/json");
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("profile").GetProperty("email").GetString()
            .ShouldBe("acct@x.test");
        doc.TryGetProperty("personal_access_tokens", out _)
            .ShouldBeTrue();
    }
}