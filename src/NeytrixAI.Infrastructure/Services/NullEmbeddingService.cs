namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Safe local fallback used when Vertex AI is not configured (e.g. VertexAI:ProjectId missing),
/// mirroring <see cref="NullAgentModelClient"/>'s fail-closed design. Never fabricates a vector -
/// throws so <c>answer_faq</c> degrades to "get a human" instead of returning a confidently wrong
/// answer ranked by a meaningless embedding.
/// </summary>
public sealed class NullEmbeddingService : IEmbeddingService
{
    public Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken cancellationToken = default) =>
        throw new EmbeddingUnavailableException("Vertex AI is not configured; embeddings are unavailable.");

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingTaskType taskType, CancellationToken cancellationToken = default) =>
        throw new EmbeddingUnavailableException("Vertex AI is not configured; embeddings are unavailable.");
}
