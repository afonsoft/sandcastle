namespace Sandcastle.Application.Conversations;

/// <summary>SPEC-169: input for one versioned plan item.</summary>
public sealed record PlanArtifactItemInput(
    string Title, IReadOnlyList<string>? Files, string? Rationale);

/// <summary>SPEC-169: input for replacing a plan artifact.</summary>
public sealed record PutPlanArtifactRequest(
    IReadOnlyList<PlanArtifactItemInput> Items);

/// <summary>SPEC-169: one reviewable plan item DTO.</summary>
public sealed record PlanArtifactItemDto(
    Guid Id, string Title, IReadOnlyList<string> Files,
    string? Rationale, string Status, IReadOnlyList<string> Comments);

/// <summary>SPEC-169: versioned plan artifact DTO.</summary>
public sealed record PlanArtifactDto(
    Guid Id, Guid ConversationId, int Version,
    DateTimeOffset? ExecutedAt, IReadOnlyList<PlanArtifactItemDto> Items);

/// <summary>SPEC-169: persisted plan artifact state machine.</summary>
public interface IPlanArtifactService
{
    /// <summary>Gets the current artifact, or null when none exists.</summary>
    Task<PlanArtifactDto?> GetAsync(Guid conversationId, CancellationToken ct);

    /// <summary>Replaces items and increments the monotonic version.</summary>
    Task<PlanArtifactDto> PutAsync(
        Guid conversationId, PutPlanArtifactRequest request, CancellationToken ct);

    /// <summary>Applies approve/reject/modify plus an optional comment.</summary>
    Task<PlanArtifactDto?> DecideAsync(
        Guid conversationId, Guid itemId, string decision,
        string? comment, CancellationToken ct);

    /// <summary>Marks the plan executed when every item is approved.</summary>
    Task<PlanArtifactDto?> ExecuteAsync(Guid conversationId, CancellationToken ct);
}

/// <summary>SPEC-169: state-machine validation failure.</summary>
public sealed class PlanArtifactStateException(string code)
    : InvalidOperationException(code)
{
    /// <summary>Stable wire error code.</summary>
    public string Code { get; } = code;
}