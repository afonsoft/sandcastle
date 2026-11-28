using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Indexing;
using Sandcastle.Application.Knowledge;
using Sandcastle.Application.Web;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Knowledge;

/// <summary>SPEC-189: scoped knowledge collections with hybrid lexical/vector retrieval.</summary>
public sealed partial class KnowledgeRagService(AppDbContext db, IEmbeddingProvider? embeddings = null, IWebSearchService? webSearch = null) : IKnowledgeRagService
{
    private static string U(Guid id) => id.ToString();
    private async Task<List<Guid>> OrgsAsync(Guid userId, CancellationToken ct) =>
        await db.OrgMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.OrgId).ToListAsync(ct);
    private async Task<bool> CanAccessAsync(KnowledgeCollectionRecord c, Guid userId, CancellationToken ct) =>
        c.OwnerUserId == U(userId) || (c.OrgId is { } org && await db.OrgMembers.AnyAsync(m => m.OrgId == org && m.UserId == userId, ct));

    public async Task<IReadOnlyList<KnowledgeCollectionInfo>> ListCollectionsAsync(Guid userId, CancellationToken ct)
    {
        var uid = U(userId); var orgs = await OrgsAsync(userId, ct);
        var rows = await db.KnowledgeCollections.AsNoTracking().Where(c => c.OwnerUserId == uid || (c.OrgId != null && orgs.Contains(c.OrgId.Value))).ToListAsync(ct);
        var result = new List<KnowledgeCollectionInfo>();
        foreach (var c in rows.OrderByDescending(c => c.UpdatedAt))
        {
            var docs = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.CollectionId == c.Id).Select(d => new { d.Id, d.ChunkCount }).ToListAsync(ct);
            result.Add(new KnowledgeCollectionInfo(c.Id, c.OrgId, c.OwnerUserId, c.Name, c.RepoPath, c.Description, docs.Count, docs.Sum(d => d.ChunkCount), c.CreatedAt, c.UpdatedAt));
        }
        return result;
    }

    public async Task<KnowledgeCollectionInfo> CreateCollectionAsync(KnowledgeCollectionUpsert request, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new KnowledgeCollectionRecord { Id = Guid.NewGuid(), OrgId = request.OrgId, OwnerUserId = U(userId), Name = request.Name.Trim(), RepoPath = request.RepoPath, Description = request.Description, CreatedAt = now, UpdatedAt = now };
        db.KnowledgeCollections.Add(row); await db.SaveChangesAsync(ct);
        return new KnowledgeCollectionInfo(row.Id, row.OrgId, row.OwnerUserId, row.Name, row.RepoPath, row.Description, 0, 0, row.CreatedAt, row.UpdatedAt);
    }

    public async Task<bool> DeleteCollectionAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var row = await db.KnowledgeCollections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null || !await CanAccessAsync(row, userId, ct)) return false;
        var docs = await db.KnowledgeDocuments.Where(d => d.CollectionId == id).ToListAsync(ct);
        var docIds = docs.Select(d => d.Id).ToList();
        db.KnowledgeChunks.RemoveRange(db.KnowledgeChunks.Where(c => docIds.Contains(c.DocumentId)));
        db.KnowledgeDocuments.RemoveRange(docs); db.KnowledgeCollections.Remove(row);
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<IReadOnlyList<KnowledgeDocumentInfo>> ListDocumentsAsync(Guid collectionId, Guid userId, CancellationToken ct)
    {
        var collection = await db.KnowledgeCollections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null || !await CanAccessAsync(collection, userId, ct)) return [];
        var rows = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.CollectionId == collectionId).ToListAsync(ct);
        return rows.OrderByDescending(d => d.CreatedAt).Select(MapDoc).ToList();
    }

    public async Task<KnowledgeDocumentInfo?> IngestDocumentAsync(Guid collectionId, KnowledgeDocumentUpsert request, Guid userId, CancellationToken ct)
    {
        var collection = await db.KnowledgeCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null || !await CanAccessAsync(collection, userId, ct)) return null;
        var now = DateTimeOffset.UtcNow;
        var safeContent = Redact(request.Content);
        var chunks = Chunk(safeContent).ToArray();
        var doc = new KnowledgeDocumentRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            Title = request.Title.Trim(),
            SourceUri = request.SourceUri.Trim(),
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safeContent)))[..24],
            ChunkCount = chunks.Length,
            LineageJson = JsonSerializer.Serialize(new { source_uri = request.SourceUri, ingested_by = U(userId), chunking = "lines40-overlap8", redacted = safeContent.Length != request.Content.Length }),
            CreatedAt = now,
        };
        db.KnowledgeDocuments.Add(doc);
        var rows = chunks.Select((text, i) => new KnowledgeChunkRecord { Id = Guid.NewGuid(), DocumentId = doc.Id, Ordinal = i, Text = text, CreatedAt = now }).ToList();
        if (embeddings is { IsConfigured: true })
        {
            try
            {
                var vectors = await embeddings.EmbedAsync(rows.Select(r => r.Text).ToArray(), ct);
                for (var i = 0; i < rows.Count && i < vectors.Count; i++) rows[i].EmbeddingJson = JsonSerializer.Serialize(vectors[i]);
            }
            catch { /* retrieval falls back lexically */ }
        }
        db.KnowledgeChunks.AddRange(rows); collection.UpdatedAt = now; await db.SaveChangesAsync(ct);
        return MapDoc(doc);
    }

    public async Task<KnowledgeQueryResponse> QueryAsync(KnowledgeQueryRequest request, Guid userId, CancellationToken ct)
    {
        var collections = await ListCollectionsAsync(userId, ct);
        if (request.CollectionId is { } cid) collections = collections.Where(c => c.Id == cid).ToList();
        if (!string.IsNullOrWhiteSpace(request.RepoPath)) collections = collections.Where(c => c.RepoPath == request.RepoPath).ToList();
        var collectionIds = collections.Select(c => c.Id).ToHashSet();
        var docs = await db.KnowledgeDocuments.AsNoTracking().Where(d => collectionIds.Contains(d.CollectionId)).ToListAsync(ct);
        var docIds = docs.Select(d => d.Id).ToHashSet();
        var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => docIds.Contains(c.DocumentId)).ToListAsync(ct);
        float[]? queryVector = null;
        if (embeddings is { IsConfigured: true })
        {
            try { queryVector = (await embeddings.EmbedAsync([request.Query], ct)).FirstOrDefault(); } catch { }
        }
        var terms = request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var citations = chunks.Select(chunk =>
        {
            var doc = docs.First(d => d.Id == chunk.DocumentId);
            var lexical = terms.Sum(t => chunk.Text.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0) / Math.Max(1, terms.Length);
            var vector = queryVector is not null && chunk.EmbeddingJson is not null ? Cosine(queryVector, JsonSerializer.Deserialize<float[]>(chunk.EmbeddingJson) ?? []) : 0.0;
            var score = vector > 0 ? (vector * .7) + (lexical * .3) : lexical;
            return new KnowledgeCitation(doc.Id, chunk.Id, doc.Title, doc.SourceUri, chunk.Ordinal, Excerpt(chunk.Text, terms), score);
        }).Where(c => c.Score > 0).ToList();
        var uid = U(userId);
        var orgIds = await OrgsAsync(userId, ct);
        var memories = await db.MemoryRecords.AsNoTracking()
            .Where(m => m.DeletedAt == null && (m.CreatedByUserId == uid || orgIds.Contains(m.OrgId)))
            .Take(200).ToListAsync(ct);
        citations.AddRange(memories.Select(m =>
        {
            var lexical = terms.Sum(t => m.Text.Contains(t, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0) / Math.Max(1, terms.Length);
            var excerpt = m.Text.Length <= 320 ? m.Text : m.Text[..320];
            return new KnowledgeCitation(m.Id, StableGuid("memory-chunk:" + m.Id), m.Key, $"memory:{m.Key}", 0, excerpt, lexical);
        }).Where(c => c.Score > 0));
        var usedWeb = false;
        if (request.IncludeWeb && webSearch is not null)
        {
            var web = await webSearch.SearchAsync(request.Query, Math.Min(5, Math.Clamp(request.Take, 1, 20)), ct);
            if (web.Ok)
            {
                usedWeb = web.Results.Count > 0;
                citations.AddRange(web.Results.Select((r, i) => new KnowledgeCitation(
                    StableGuid("web-doc:" + r.Url), StableGuid("web-chunk:" + r.Url), r.Title, r.Url, i,
                    r.Snippet.Length <= 320 ? r.Snippet : r.Snippet[..320], .65 - (i * .05))));
            }
        }
        citations = citations.OrderByDescending(c => c.Score).Take(Math.Clamp(request.Take, 1, 20)).ToList();
        var mode = queryVector is not null ? "hybrid" : "lexical";
        if (usedWeb) mode += "+web";
        return new KnowledgeQueryResponse(request.Query, mode, citations, embeddings?.IsConfigured == true);
    }

    private static KnowledgeDocumentInfo MapDoc(KnowledgeDocumentRecord d) => new(d.Id, d.CollectionId, d.Title, d.SourceUri, d.ContentHash, d.ChunkCount, d.LineageJson, d.CreatedAt);
    private static IEnumerable<string> Chunk(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i += 32)
            yield return string.Join('\n', lines.Skip(i).Take(40));
    }
    private static string Redact(string text) => SecretRegex().Replace(text, "[redacted]");
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa == 0 || bb == 0 ? 0 : dot / (Math.Sqrt(aa) * Math.Sqrt(bb));
    }
    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash[..16]);
    }

    private static string Excerpt(string text, string[] terms)
    {
        var idx = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        return text.Length <= 320 ? text : text[idx..Math.Min(text.Length, idx + 320)];
    }
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{8,}|(?:api[_-]?key|token|secret)\s*[:=]\s*\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();
}