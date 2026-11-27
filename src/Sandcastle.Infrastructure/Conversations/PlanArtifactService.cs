using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Conversations;

/// <summary>SPEC-169: EF-backed versioned plan artifact state machine.</summary>
public sealed class PlanArtifactService(AppDbContext db) : IPlanArtifactService
{
    private static readonly HashSet<string> Decisions =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "reject", "modify" };

    /// <inheritdoc />
    public async Task<PlanArtifactDto?> GetAsync(
        Guid conversationId, CancellationToken ct) =>
        ToDto(await Query(conversationId).FirstOrDefaultAsync(ct));

    /// <inheritdoc />
    public async Task<PlanArtifactDto> PutAsync(
        Guid conversationId, PutPlanArtifactRequest request, CancellationToken ct)
    {
        var plan = await Query(conversationId).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (plan is null)
        {
            plan = new ConversationPlanArtifactRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ConversationPlanArtifacts.Add(plan);
        }
        else if (plan.ExecutedAt is not null)
        {
            throw new PlanArtifactStateException("plan_already_executed");
        }
        else
        {
            var version = plan.Version;
            await db.ConversationPlanArtifacts.Where(p => p.Id == plan.Id)
                .ExecuteDeleteAsync(ct);
            db.ChangeTracker.Clear();
            plan = new ConversationPlanArtifactRecord
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                Version = version,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ConversationPlanArtifacts.Add(plan);
        }

        plan.Version++;
        plan.UpdatedAt = now;
        var position = 0;
        foreach (var input in request.Items)
        {
            if (string.IsNullOrWhiteSpace(input.Title))
            {
                throw new PlanArtifactStateException("plan_item_title_required");
            }

            plan.Items.Add(new ConversationPlanItemRecord
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                Position = position++,
                Title = input.Title.Trim(),
                FilesJson = JsonSerializer.Serialize(input.Files ?? []),
                Rationale = input.Rationale,
                Status = "pending",
                CommentsJson = "[]",
            });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(plan)!;
    }

    /// <inheritdoc />
    public async Task<PlanArtifactDto?> DecideAsync(
        Guid conversationId, Guid itemId, string decision,
        string? comment, CancellationToken ct)
    {
        if (!Decisions.Contains(decision))
        {
            throw new PlanArtifactStateException("plan_decision_invalid");
        }

        var plan = await Query(conversationId).FirstOrDefaultAsync(ct);
        if (plan is null)
        {
            return null;
        }

        if (plan.ExecutedAt is not null)
        {
            throw new PlanArtifactStateException("plan_already_executed");
        }

        var item = plan.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null)
        {
            return null;
        }

        item.Status = decision.ToLowerInvariant() switch
        {
            "approve" => "approved",
            "reject" => "rejected",
            _ => "modified",
        };
        if (!string.IsNullOrWhiteSpace(comment))
        {
            var comments = JsonSerializer.Deserialize<List<string>>(
                item.CommentsJson) ?? [];
            comments.Add(comment.Trim());
            item.CommentsJson = JsonSerializer.Serialize(comments);
        }

        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(plan);
    }

    /// <inheritdoc />
    public async Task<PlanArtifactDto?> ExecuteAsync(
        Guid conversationId, CancellationToken ct)
    {
        var plan = await Query(conversationId).FirstOrDefaultAsync(ct);
        if (plan is null)
        {
            return null;
        }

        if (plan.ExecutedAt is not null)
        {
            throw new PlanArtifactStateException("plan_already_executed");
        }

        if (plan.Items.Count == 0 || plan.Items.Any(i => i.Status != "approved"))
        {
            throw new PlanArtifactStateException("plan_not_ready");
        }

        plan.ExecutedAt = DateTimeOffset.UtcNow;
        plan.UpdatedAt = plan.ExecutedAt.Value;
        await db.SaveChangesAsync(ct);
        return ToDto(plan);
    }

    private IQueryable<ConversationPlanArtifactRecord> Query(Guid conversationId) =>
        db.ConversationPlanArtifacts.Include(p => p.Items)
            .Where(p => p.ConversationId == conversationId);

    private static PlanArtifactDto? ToDto(ConversationPlanArtifactRecord? plan)
    {
        if (plan is null)
        {
            return null;
        }

        return new PlanArtifactDto(plan.Id, plan.ConversationId, plan.Version,
            plan.ExecutedAt, plan.Items.OrderBy(i => i.Position).Select(i =>
                new PlanArtifactItemDto(i.Id, i.Title,
                    JsonSerializer.Deserialize<List<string>>(i.FilesJson) ?? [],
                    i.Rationale, i.Status,
                    JsonSerializer.Deserialize<List<string>>(i.CommentsJson) ?? []))
                .ToList());
    }
}