namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Distinguishes how an embedding will be used, per Vertex AI's text embedding API. Using the
/// matching task type for documents at ingestion time versus queries at search time measurably
/// improves retrieval quality for embedding models trained with asymmetric objectives (the
/// document and query encoders are nudged apart even though they share one model).
/// </summary>
public enum EmbeddingTaskType
{
    RetrievalDocument,
    RetrievalQuery,
}

/// <summary>
/// Converts text into a dense vector for pgvector similarity search (GAP-04). Implementations
/// must return vectors whose length matches <c>knowledge_chunks.embedding</c>'s declared
/// dimension (1536, see db/migrations/001_initial_schema.sql) - a mismatched dimension fails at
/// the database layer with a clear Postgres error rather than silently corrupting rankings.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batched form for ingestion. Implementations should send these as one provider call where
    /// the provider supports it (Vertex AI's Predict endpoint accepts multiple instances per
    /// request) instead of looping <see cref="EmbedAsync"/> one text at a time.
    /// </summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingTaskType taskType, CancellationToken cancellationToken = default);
}
