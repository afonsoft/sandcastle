using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-180 RF-002: typed provider connections + official model discovery.</summary>
public sealed class Spec180ProviderConnectionEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-pc-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly RecordingHandler _handler = new();

    public Spec180ProviderConnectionEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath};Default Timeout=10");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
            builder.UseSetting("Sandbox:InstanceOwner", $"test-{Guid.NewGuid():N}");
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(
                    new StubFactory(_handler))));
        });
        _http = TestAuth.Login(_factory);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Crud_Roundtrip_Stores_Secret_By_Reference_And_Never_Echos_Value()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "Work OpenAI",
            provider = "openai",
            secret_value = "test-secret-1234567890abcdef",
        });

        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await create.Content.ReadAsStringAsync();
        body.ShouldNotContain("test-secret-1234567890abcdef");
        var doc = JsonDocument.Parse(body).RootElement;
        var id = doc.GetProperty("id").GetGuid();
        doc.GetProperty("secret_ref").GetString().ShouldStartWith("provider_connection_");
        doc.GetProperty("provider").GetString().ShouldBe("openai");
        doc.GetProperty("base_url").GetString().ShouldBe("https://api.openai.com/v1");

        var list = await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/provider-connections");
        list!.Single().GetProperty("id").GetGuid().ShouldBe(id);

        var update = await _http.PutAsJsonAsync($"/api/v1/provider-connections/{id}", new
        {
            name = "Renamed",
            enabled = false,
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();

        (await _http.DeleteAsync($"/api/v1/provider-connections/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _http.GetFromJsonAsync<JsonElement[]>("/api/v1/provider-connections"))!
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Validation_User_Isolation_And_Authz_Are_Enforced()
    {
        (await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "bad",
            provider = "bogus",
            secret_value = "sk-x",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "bad url",
            provider = "openai_compatible",
            base_url = "http://evil.example.com",
            secret_value = "sk-x",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "missing",
            provider = "openai",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var create = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "mine",
            provider = "openai",
            secret_value = "sk-owner",
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var other = TestAuth.Login(_factory, "other@example.com");
        (await other.GetFromJsonAsync<JsonElement[]>("/api/v1/provider-connections"))!
            .ShouldBeEmpty();
        (await other.DeleteAsync($"/api/v1/provider-connections/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        other.Dispose();

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/provider-connections"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var secretless = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "local ollama",
            provider = "ollama",
        });
        secretless.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secretlessId = (await secretless.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await _http.PutAsJsonAsync($"/api/v1/provider-connections/{secretlessId}", new
        {
            provider = "openai",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OrgScoped_Connections_Are_Readable_But_Not_Writable_By_Member()
    {
        var create = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "org openai",
            scope = "org",
            provider = "openai",
            secret_value = "sk-org-secret",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.SingleAsync(u => u.Email == "test@local");
        var orgId = Guid.Parse(owner.CurrentOrgId!);
        var member = new UserRecord
        {
            Id = Guid.NewGuid(),
            Email = "member180@local",
            DisplayName = "member180",
            RolesJson = "[]",
            CurrentOrgId = orgId.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(member);
        db.OrgMembers.Add(new OrgMemberRecord
        {
            OrgId = orgId,
            UserId = member.Id,
            Role = "member",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        using var memberHttp = TestAuth.Login(_factory, "member180@local");
        var listBody = await memberHttp.GetAsync("/api/v1/provider-connections");
        listBody.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listText = await listBody.Content.ReadAsStringAsync();
        listText.ShouldContain(id.ToString());
        listText.ShouldNotContain("sk-org-secret");

        (await memberHttp.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "member org write",
            scope = "org",
            provider = "openai",
            secret_value = "sk-x",
        })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await memberHttp.PutAsJsonAsync($"/api/v1/provider-connections/{id}", new
        {
            name = "forbidden rename",
        })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await memberHttp.DeleteAsync($"/api/v1/provider-connections/{id}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Test_And_Models_Use_Provider_Official_Endpoints_Without_Leaking_Secrets()
    {
        _handler.Body = """{"data":[{"id":"gpt-5"},{"id":"gpt-5-mini"}]}""";
        var create = await _http.PostAsJsonAsync("/api/v1/provider-connections", new
        {
            name = "openai",
            provider = "openai",
            secret_value = "sk-test-9999",
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var test = await _http.PostAsync($"/api/v1/provider-connections/{id}/test", null);
        test.StatusCode.ShouldBe(HttpStatusCode.OK);
        var testDoc = await test.Content.ReadFromJsonAsync<JsonElement>();
        testDoc.GetProperty("ok").GetBoolean().ShouldBeTrue();
        testDoc.GetProperty("model_count").GetInt32().ShouldBe(2);
        _handler.LastPath.ShouldBe("/v1/models");
        _handler.LastAuth.ShouldBe("Bearer sk-test-9999");

        var models = await _http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/provider-connections/{id}/models");
        models.GetProperty("models").EnumerateArray()
            .Select(m => m.GetString()).ShouldBe(["gpt-5", "gpt-5-mini"]);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; set; } = """{"data":[]}""";
        public string? LastPath { get; private set; }
        public string? LastAuth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}