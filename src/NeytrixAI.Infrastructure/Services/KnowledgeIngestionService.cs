using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Infrastructure.Services;

public sealed class KnowledgeIngestionService : IKnowledgeIngestionService
{
    private readonly IEmbeddingService _embeddings;
    private readonly IKnowledgeChunkRepository _knowledgeChunks;

    public KnowledgeIngestionService(IEmbeddingService embeddings, IKnowledgeChunkRepository knowledgeChunks)
    {
        _embeddings = embeddings;
        _knowledgeChunks = knowledgeChunks;
    }

    public async Task<Guid> IngestAsync(
        Guid tenantId, string sourceType, string content, string? sourceRef = null, string metadataJson = "{}",
        CancellationToken cancellationToken = default)
    {
        var embedding = await _embeddings.EmbedAsync(content, EmbeddingTaskType.RetrievalDocument, cancellationToken);
        var chunk = KnowledgeChunk.Create(tenantId, sourceType, content, embedding, sourceRef, metadataJson);
        return await _knowledgeChunks.CreateAsync(chunk, cancellationToken);
    }
}
