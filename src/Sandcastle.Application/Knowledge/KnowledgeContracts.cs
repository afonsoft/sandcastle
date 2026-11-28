namespace Sandcastle.Application.Knowledge;

/// <summary>SPEC-63: estado da sugestão na fila de revisão.</summary>
public enum SuggestionStatus { Pending, Approved, Dismissed }

/// <summary>Tipo da sugestão: entry nova, merge proposto, conflito.</summary>
public enum SuggestionKind { Entry, Merge, Conflict }

public sealed record KnowledgeSuggestionInfo(
    Guid Id, Guid? OrgId, string Scope, string? RepoPath,
    string Title, string Content, SuggestionKind Kind,
    SuggestionStatus Status, string? Reason,
    IReadOnlyList<Guid> SourceConversationIds,
    IReadOnlyList<Guid> SourceEntryIds,
    string? Resolution, DateTimeOffset CreatedAt);

/// <summary>Relatório do groom (RF-003).</summary>
public sealed record KnowledgeGroomReport(
    IReadOnlyList<(Guid A, Guid B, double Similarity)> Duplicates,
    IReadOnlyList<(Guid A, Guid B)> Conflicts,
    IReadOnlyList<Guid> Stale);

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSuggestionInfo>> ListSuggestionsAsync(
        SuggestionStatus? status, string? scope, CancellationToken ct);
    Task<KnowledgeSuggestionInfo> SuggestAsync( // NOSONAR S107 — SPEC-104 (decisão aprovada): contrato público upstream-parity; param object mudaria API
        string title, string content, string scope, string? repoPath,
        SuggestionKind kind, string? reason,
        IReadOnlyList<Guid>? sourceConversationIds,
        IReadOnlyList<Guid>? sourceEntryIds, CancellationToken ct);
    Task<KnowledgeSuggestionInfo> ApproveAsync(
        Guid id, string? contentOverride, string? actorUserId,
        CancellationToken ct);
    Task<KnowledgeSuggestionInfo> DismissAsync(
        Guid id, string? reason, CancellationToken ct);
    Task<KnowledgeGroomReport> GroomAsync(CancellationToken ct);
}
/// <summary>SPEC-189: knowledge collection scoped by user/org/repo.</summary>
public sealed record KnowledgeCollectionInfo(Guid Id, Guid? OrgId, string OwnerUserId, string Name, string? RepoPath, string? Description, int DocumentCount, int ChunkCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
/// <summary>SPEC-189: collection mutation payload.</summary>
public sealed record KnowledgeCollectionUpsert(string Name, string? RepoPath, string? Description, Guid? OrgId);
/// <summary>SPEC-189: ingested document.</summary>
public sealed record KnowledgeDocumentInfo(Guid Id, Guid CollectionId, string Title, string SourceUri, string ContentHash, int ChunkCount, string LineageJson, DateTimeOffset CreatedAt);
/// <summary>SPEC-189: document ingestion payload.</summary>
public sealed record KnowledgeDocumentUpsert(string Title, string SourceUri, string Content);
/// <summary>SPEC-189: retrieval citation.</summary>
public sealed record KnowledgeCitation(Guid DocumentId, Guid ChunkId, string Title, string SourceUri, int Ordinal, string Excerpt, double Score);
/// <summary>SPEC-189: retrieval response.</summary>
public sealed record KnowledgeQueryResponse(string Query, string Mode, IReadOnlyList<KnowledgeCitation> Citations, bool EmbeddingAvailable);
/// <summary>SPEC-189: retrieval request.</summary>
public sealed record KnowledgeQueryRequest(string Query, Guid? CollectionId, string? RepoPath, int Take, bool IncludeWeb = false);

public interface IKnowledgeRagService
{
    Task<IReadOnlyList<KnowledgeCollectionInfo>> ListCollectionsAsync(Guid userId, CancellationToken ct);
    Task<KnowledgeCollectionInfo> CreateCollectionAsync(KnowledgeCollectionUpsert request, Guid userId, CancellationToken ct);
    Task<bool> DeleteCollectionAsync(Guid id, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<KnowledgeDocumentInfo>> ListDocumentsAsync(Guid collectionId, Guid userId, CancellationToken ct);
    Task<KnowledgeDocumentInfo?> IngestDocumentAsync(Guid collectionId, KnowledgeDocumentUpsert request, Guid userId, CancellationToken ct);
    Task<KnowledgeQueryResponse> QueryAsync(KnowledgeQueryRequest request, Guid userId, CancellationToken ct);
}