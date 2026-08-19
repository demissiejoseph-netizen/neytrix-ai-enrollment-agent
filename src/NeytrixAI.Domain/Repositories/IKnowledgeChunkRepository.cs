using NeytrixAI.Domain.Entities;

namespace NeytrixAI.Domain.Repositories;

/// <summary>Ranked hit from a similarity search, closest first. <c>Distance</c> is cosine distance (0 = identical, 2 = opposite) per the ivfflat vector_cosine_ops index on knowledge_chunks.</summary>
public sealed record KnowledgeChunkMatch(Guid Id, string Content, double Distance);

public interface IKnowledgeChunkRepository
{
    /// <summary>Inserts a new knowledge chunk with its embedding. Ingestion-only - not exposed as one of the 11 model-facing tools.</summary>
    Task<Guid> CreateAsync(KnowledgeChunk chunk, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cosine-similarity search over a tenant's knowledge_chunks, restricted to the given
    /// source types, nearest first. Backs answer_faq's real RAG retrieval (GAP-04).
    /// </summary>
    Task<IReadOnlyList<KnowledgeChunkMatch>> SearchAsync(
        Guid tenantId,
        float[] queryEmbedding,
        IReadOnlyList<string> sourceTypes,
        int limit,
        CancellationToken cancellationToken = default);
}
