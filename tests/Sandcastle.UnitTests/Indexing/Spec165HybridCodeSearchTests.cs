using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Indexing;
using Sandcastle.Infrastructure.Indexing;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Indexing;

public sealed class Spec165HybridCodeSearchTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-spec165-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) { File.Delete(_dbPath); }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BuscaSemantica_Traz_Unit_Sem_Match_Lexical()
    {
        var repoIndex = Guid.NewGuid();
        var semantic = Unit(repoIndex, "auth/login.cs",
            "validates user credentials and issues session tokens", "auth");
        var other = Unit(repoIndex, "billing/invoice.cs",
            "calculates invoice totals", "invoice");
        var provider = new FakeEmbeddingProvider(text =>
            text.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || text.Contains("credentials", StringComparison.OrdinalIgnoreCase)
                ? [1f, 0f] : [0f, 1f]);
        var store = new EfCodeSearchStore(_db, provider);
        await store.IndexUnitsAsync(repoIndex, [semantic, other]);

        var results = await store.SearchAsync(repoIndex, "authentication", 5);

        results.ShouldNotBeEmpty();
        results[0].Unit.Path.ShouldBe("auth/login.cs");
        results[0].Degraded.ShouldBeFalse();
        results[0].Excerpt.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SemProvider_Degrada_Para_Lexical_Com_Flag()
    {
        var repoIndex = Guid.NewGuid();
        var store = new EfCodeSearchStore(_db);
        await store.IndexUnitsAsync(repoIndex,
            [Unit(repoIndex, "src/user.cs", "user authentication code", "auth")]);

        var results = await store.SearchAsync(repoIndex, "authentication", 5);

        results.ShouldNotBeEmpty();
        results[0].Degraded.ShouldBeTrue();
    }

    [Fact]
    public async Task FalhaDeEmbedding_Nao_Derruba_Busca_Nem_Indexacao()
    {
        var repoIndex = Guid.NewGuid();
        var provider = new FakeEmbeddingProvider(_ => throw new InvalidOperationException("boom"));
        var store = new EfCodeSearchStore(_db, provider);
        await store.IndexUnitsAsync(repoIndex,
            [Unit(repoIndex, "src/user.cs", "user authentication code", "auth")]);

        var results = await store.SearchAsync(repoIndex, "authentication", 5);

        results.ShouldNotBeEmpty();
        results[0].Degraded.ShouldBeTrue();
    }

    [Fact]
    public async Task Budget_Respeita_MaxEmbeddingsPerCall_E_ReusaEmbedding()
    {
        var repoIndex = Guid.NewGuid();
        var provider = new FakeEmbeddingProvider(_ => [1f, 0f]);
        var options = Microsoft.Extensions.Options.Options.Create(
            new SearchOptions { MaxEmbeddingsPerCall = 2 });
        var store = new EfCodeSearchStore(_db, provider, options);
        var units = Enumerable.Range(0, 5)
            .Select(i => Unit(repoIndex, $"src/f{i}.cs", $"code {i}", $"s{i}"))
            .ToList();
        units[0] = units[0] with
        {
            EmbeddingJson = JsonSerializer.Serialize(new[] { 1f, 0f }),
        };

        await store.IndexUnitsAsync(repoIndex, units);

        provider.Calls.Count.ShouldBe(2);
        provider.Calls.ShouldAllBe(count => count <= 2);
        var stored = await _db.CodeUnits.CountAsync(
            u => u.RepoIndexId == repoIndex && u.EmbeddingJson != null);
        stored.ShouldBe(5);
    }

    private static CodeUnitDto Unit(
        Guid repoIndexId, string path, string text, string symbol) =>
        new(Guid.NewGuid(), repoIndexId, path, 1, 20,
            "symbol", symbol, text, $"{path}-{text}");

    private sealed class FakeEmbeddingProvider(Func<string, float[]> vectorize)
        : IEmbeddingProvider
    {
        public bool IsConfigured => true;
        public List<int> Calls { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Calls.Add(texts.Count);
            IReadOnlyList<float[]> vectors = texts
                .Select(t => vectorize(t)).ToArray();
            return Task.FromResult(vectors);
        }
    }
}