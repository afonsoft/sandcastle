using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-192 RF-006: composed-host registry coverage for document readers.</summary>
public sealed class Spec192DocumentToolsIntegrationTests
{
    [Fact]
    public void Composed_host_resolves_document_reader_tools()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString",
                $"Data Source={Path.Combine(Path.GetTempPath(), $"sc192-{Guid.NewGuid():N}.db")}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
        });
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.Find("read_pdf").ShouldNotBeNull();
        registry.Find("read_archive").ShouldNotBeNull();
        registry.Find("read_sqlite").ShouldNotBeNull();
    }
}