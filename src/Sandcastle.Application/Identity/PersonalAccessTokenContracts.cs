namespace Sandcastle.Application.Identity;

/// <summary>
/// SPEC-158 RF-001/RF-003: personal access token (<c>sc_pat_*</c>) —
/// hash-only at rest (SHA-256), escopos <c>read</c>|<c>write</c>; escopo
/// <c>read</c> sozinho bloqueia métodos mutantes (middleware → 403
/// <c>insufficient_scope</c>). O token cru é retornado uma única vez no
/// create.
/// </summary>
public sealed record PatResolution(
    Guid TokenId,
    Guid UserId,
    IReadOnlyList<string> Scopes);

/// <summary>Resumo de PAT (nunca inclui hash nem token cru).</summary>
public sealed record PatSummary(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Geração/validação/revogação de PATs.</summary>
public interface IPersonalAccessTokenService
{
    /// <summary>Prefixo público dos tokens pessoais.</summary>
    public const string TokenPrefix = "sc_pat_";

    /// <summary>Cria um PAT; retorna o token cru exibido uma única vez.</summary>
    Task<(Guid Id, string RawToken)> CreateAsync(
        Guid userId, string name, IReadOnlyList<string> scopes,
        DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>Valida um token cru; null se inválido/revogado/expirado.</summary>
    Task<PatResolution?> ValidateAsync(string rawToken, CancellationToken ct);

    /// <summary>Lista PATs ativos (não revogados) do usuário.</summary>
    Task<IReadOnlyList<PatSummary>> ListAsync(
        Guid userId, CancellationToken ct);

    /// <summary>Revoga um PAT do usuário; false se não encontrado.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken ct);
}