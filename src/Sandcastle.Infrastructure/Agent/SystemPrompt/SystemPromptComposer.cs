using Microsoft.Extensions.Logging;

using Sandcastle.Application.Agent;

namespace Sandcastle.Infrastructure.Agent.SystemPrompt;

/// <summary>
/// Concatenates sections from <see cref="IAgentPromptContributor"/>s in
/// ascending <see cref="IAgentPromptContributor.Order"/> (SPEC-90 RF-001).
/// A throwing contributor is tolerated (warning logged, section omitted) —
/// see SPEC-90 NFR-002. SPEC-194 RF-001 groups cache-stable sections before
/// run-dependent sections so providers can reuse a stable prompt prefix.
/// </summary>
public sealed class SystemPromptComposer(
    IEnumerable<IAgentPromptContributor> contributors,
    ILogger<SystemPromptComposer> logger)
{
    public async Task<string> ComposeAsync(AgentPromptContext ctx)
        => (await ComposeDetailedAsync(ctx)).Text;

    /// <summary>
    /// SPEC-194 RF-002: composes the prompt and returns per-section cache
    /// metadata for diagnostics/events. Provider wire behavior is unchanged;
    /// only ordering and diagnostics are introduced here.
    /// </summary>
    public async Task<PromptComposition> ComposeDetailedAsync(AgentPromptContext ctx)
    {
        var sections = new List<(string Text, PromptSectionInfo Info)>();
        var ordered = contributors
            .OrderBy(c => c.CacheTier)
            .ThenBy(c => c.Order);

        foreach (var contributor in ordered)
        {
            IReadOnlyList<string> contributed;
            try
            {
                contributed = await contributor.ContributeAsync(ctx);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Prompt contributor {Contributor} failed — section omitted",
                    contributor.GetType().Name);
                continue;
            }

            foreach (var section in contributed
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.TrimEnd()))
            {
                sections.Add((section, new PromptSectionInfo(
                    contributor.GetType().Name,
                    contributor.Order,
                    contributor.CacheTier,
                    section.Length)));
            }
        }

        if (sections.Count == 0)
        {
            return new PromptComposition(string.Empty, [], 0, 0);
        }

        var text = string.Join("\n\n", sections.Select(s => s.Text)) + "\n";
        return new PromptComposition(
            text,
            sections.Select(s => s.Info).ToList(),
            sections.Where(s => s.Info.Tier == PromptCacheTier.Static)
                .Sum(s => s.Info.Chars),
            sections.Where(s => s.Info.Tier == PromptCacheTier.Dynamic)
                .Sum(s => s.Info.Chars));
    }
}