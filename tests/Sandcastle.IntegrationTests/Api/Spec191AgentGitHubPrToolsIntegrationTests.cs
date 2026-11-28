using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Scm;

using Shouldly;

namespace Sandcastle.IntegrationTests.Api;

/// <summary>SPEC-191 RF-006: tool catalog/service wiring in the composed API host.</summary>
public sealed class Spec191AgentGitHubPrToolsIntegrationTests
{
    [Fact]
    public void Composed_host_resolves_github_pr_tools_and_service()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString",
                $"Data Source={Path.Combine(Path.GetTempPath(), $"sc191-{Guid.NewGuid():N}.db")}");
            builder.UseSetting("Observability:OtlpEndpoint", string.Empty);
        });
        using var scope = factory.Services.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.Find("github_list_pull_requests").ShouldNotBeNull();
        registry.Find("github_view_pull_request").ShouldNotBeNull();
        registry.Find("github_read_pull_request_diff").ShouldNotBeNull();
        registry.Find("github_comment_pull_request").ShouldNotBeNull();
        scope.ServiceProvider.GetService<ScmAgentPullRequestService>().ShouldNotBeNull();
    }
}