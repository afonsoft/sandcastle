using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Indexing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Indexing;

public sealed class EfCodeSearchStore : ICodeSearchStore
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IOptions<SearchOptions> _searchOptions;
    private readonly ILogger<EfCodeSearchStore> _logger;

    public EfCodeSearchStore(
        AppDbContext db,
        IEmbeddingProvider? embeddingProvider = null,
        IOptions<SearchOptions>? searchOptions = null,
        ILogger<EfCodeSearchStore>? logger = null)
    {
        _db = db;
        _embeddingProvider = embeddingProvider;
        _searchOptions = searchOptions
            ?? Microsoft.Extensions.Options.Options.Create(new SearchOptions());
        _logger = logger ?? NullLogger<EfCodeSearchStore>.Instance;
    }

    public async Task IndexUnitsAsync(Guid repoIndexId, IEnumerable<CodeUnitDto> units, CancellationToken ct = default)
    {
        var unitList = units.ToList();
        var generatedEmbeddings = await GenerateMissingEmbeddingsAsync(unitList, ct);
        var records = unitList.Select(u => new CodeUnitRecord
        {
            Id = u.Id,
            RepoIndexId = repoIndexId,
            Path = u.Path,
            StartLine = u.StartLine,
            EndLine = u.EndLine,
            Kind = u.Kind,
            SymbolName = u.SymbolName,
            Text = u.Text,
            Hash = u.Hash,
            EmbeddingJson = u.EmbeddingJson
                ?? (generatedEmbeddings.TryGetValue(u.Id, out var embedding)
                    ? JsonSerializer.Serialize(embedding) : null),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.CodeUnits.AddRangeAsync(records, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<Guid, float[]>> GenerateMissingEmbeddingsAsync(
        IReadOnlyList<CodeUnitDto> units, CancellationToken ct)
    {
        var result = new Dictionary<Guid, float[]>();
        if (_embeddingProvider is not { IsConfigured: true })
        {
            return result;
        }

        var batchSize = Math.Max(1, _searchOptions.Value.MaxEmbeddingsPerCall);
        foreach (var batch in units
            .Where(u => u.EmbeddingJson is null)
            .Chunk(batchSize))
        {
            try
            {
                var vectors = await _embeddingProvider.EmbedAsync(
                    batch.Select(u => EmbeddingText(u)).ToArray(), ct);
                for (var i = 0; i < Math.Min(batch.Length, vectors.Count); i++)
                {
                    result[batch[i].Id] = vectors[i];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SPEC-165 guardrail: indexing never fails because embeddings failed.
                _logger.LogWarning(ex,
                    "embedding generation failed; indexing continues lexically");
                break;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<SearchCodeResult>> SearchAsync(Guid repoIndexId, string query, int limit = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchCodeResult>();
        }

        var terms = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .Take(5)
            .ToList();

        var candidates = await _db.CodeUnits.AsNoTracking()
            .Where(c => c.RepoIndexId == repoIndexId)
            .Take(2000)
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return Array.Empty<SearchCodeResult>();
        }

        var lexical = candidates.Select(c => new
        {
            Unit = c,
            Score = LexicalScore(c, query, terms),
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Unit.Path, StringComparer.Ordinal)
        .ToList();

        var semanticAvailable = false;
        var semanticRanked = new List<(CodeUnitRecord Unit, double Score)>();
        if (_embeddingProvider is { IsConfigured: true }
            && candidates.Any(c => c.EmbeddingJson is not null))
        {
            try
            {
                var queryVectors = await _embeddingProvider.EmbedAsync([query], ct);
                if (queryVectors.Count > 0)
                {
                    var queryVector = queryVectors[0];
                    semanticRanked = candidates
                        .Select(c => (Unit: c, Score: Cosine(queryVector, Deserialize(c.EmbeddingJson))))
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Unit.Path, StringComparer.Ordinal)
                        .ToList();
                    semanticAvailable = semanticRanked.Count > 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "query embedding failed; search degrades to lexical");
            }
        }

        var ordered = semanticAvailable
            ? FuseRrf(lexical.Select(x => (x.Unit, x.Score)).ToList(),
                semanticRanked, _searchOptions.Value.RrfK)
            : lexical.Select(x => (x.Unit, x.Score)).ToList();

        return ordered.Take(limit).Select(x =>
        {
            var snippet = x.Unit.Text.Length > 300
                ? x.Unit.Text[..300] + "..." : x.Unit.Text;
            var dto = new CodeUnitDto(x.Unit.Id, x.Unit.RepoIndexId,
                x.Unit.Path, x.Unit.StartLine, x.Unit.EndLine, x.Unit.Kind,
                x.Unit.SymbolName, x.Unit.Text, x.Unit.Hash,
                x.Unit.EmbeddingJson);
            return new SearchCodeResult(
                dto, x.Score, snippet, !semanticAvailable, snippet);
        }).ToList();
    }

    private static List<(CodeUnitRecord Unit, double Score)> FuseRrf(
        IReadOnlyList<(CodeUnitRecord Unit, double Score)> lexical,
        IReadOnlyList<(CodeUnitRecord Unit, double Score)> semantic,
        int rrfK)
    {
        var scores = new Dictionary<Guid, (CodeUnitRecord Unit, double Score)>();
        static void AddRanks(
            IReadOnlyList<(CodeUnitRecord Unit, double Score)> ranked,
            int rrfK,
            Dictionary<Guid, (CodeUnitRecord Unit, double Score)> scores)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var item = ranked[i];
                var contribution = 1.0 / (rrfK + i + 1);
                if (scores.TryGetValue(item.Unit.Id, out var existing))
                {
                    scores[item.Unit.Id] = existing with
                    {
                        Score = existing.Score + contribution,
                    };
                }
                else
                {
                    scores[item.Unit.Id] = (item.Unit, contribution);
                }
            }
        }

        AddRanks(lexical, rrfK, scores);
        AddRanks(semantic, rrfK, scores);
        return scores.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Unit.Path, StringComparer.Ordinal)
            .Select(x => (x.Unit, x.Score))
            .ToList();
    }

    private static double LexicalScore(
        CodeUnitRecord unit, string query, IReadOnlyList<string> terms)
    {
        double score = 0;
        if (unit.SymbolName != null
            && unit.SymbolName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 10.0;
        }

        if (unit.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 5.0;
        }

        score += terms.Count(t => unit.Text.Contains(
            t, StringComparison.OrdinalIgnoreCase));
        return score;
    }

    private static string EmbeddingText(CodeUnitDto unit) =>
        $"{unit.SymbolName}\n{unit.Text}";

    private static float[]? Deserialize(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<float[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Cosine(float[] query, float[]? candidate)
    {
        if (candidate is null || candidate.Length == 0
            || query.Length != candidate.Length)
        {
            return 0;
        }

        double dot = 0;
        double queryNorm = 0;
        double candidateNorm = 0;
        for (var i = 0; i < query.Length; i++)
        {
            dot += query[i] * candidate[i];
            queryNorm += query[i] * query[i];
            candidateNorm += candidate[i] * candidate[i];
        }

        return queryNorm == 0 || candidateNorm == 0
            ? 0 : dot / (Math.Sqrt(queryNorm) * Math.Sqrt(candidateNorm));
    }

    public async Task DeleteIndexUnitsAsync(Guid repoIndexId, CancellationToken ct = default)
    {
        var units = await _db.CodeUnits.Where(u => u.RepoIndexId == repoIndexId).ToListAsync(ct);
        if (units.Count > 0)
        {
            _db.CodeUnits.RemoveRange(units);
            await _db.SaveChangesAsync(ct);
        }
    }
}