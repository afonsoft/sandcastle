using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Indexing;
using Sandcastle.Infrastructure.Agent;

namespace Sandcastle.Infrastructure.Indexing;

/// <summary>
/// SPEC-165 RF-001: OpenAI-compatible embeddings client. Uses the same
/// gateway/credentials as chat by default and resolves the model through the
/// canonical <see cref="LlmRole.Embedding"/> router role.
/// </summary>
public sealed class OpenAiCompatEmbeddingProvider(
    IHttpClientFactory httpFactory,
    IOptions<AgentLlmOptions> agentOptions,
    IOptions<EmbeddingOptions> embeddingOptions,
    IModelRouter router,
    ILogger<OpenAiCompatEmbeddingProvider> logger) : IEmbeddingProvider
{
    private const string DefaultLiteLlmBaseUrl = "http://localhost:4000"; // NOSONAR S1075 — local default

    /// <inheritdoc />
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(embeddingOptions.Value.Model);

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (!IsConfigured || texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var preferred = embeddingOptions.Value.Model!;
        var selection = await router.ResolveAsync(
            LlmRole.Embedding,
            new LlmRouteContext(PreferredModel: preferred), ct);
        var model = selection?.Model ?? preferred;
        var cfg = agentOptions.Value;
        var embeddings = embeddingOptions.Value;
        var baseUrl = (embeddings.BaseUrl ?? cfg.BaseUrl
            ?? DefaultLiteLlmBaseUrl).TrimEnd('/');
        var apiKey = embeddings.ApiKey ?? cfg.ApiKey;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl}/embeddings");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var payload = JsonSerializer.Serialize(new
        {
            model,
            input = texts,
        });
        request.Content = new StringContent(
            payload, Encoding.UTF8, "application/json");

        var client = httpFactory.CreateClient("agent-llm");
        client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = SecretRedactor.Redact(
                await response.Content.ReadAsStringAsync(ct));
            throw new HttpRequestException(
                $"embeddings request failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var data = doc.RootElement.GetProperty("data");
        var vectors = new float[data.GetArrayLength()][];
        var i = 0;
        foreach (var item in data.EnumerateArray())
        {
            vectors[i++] = item.GetProperty("embedding")
                .EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }

        if (vectors.Length != texts.Count)
        {
            logger.LogWarning(
                "Embedding provider returned {Got} vectors for {Expected} texts",
                vectors.Length, texts.Count);
        }

        return vectors;
    }
}