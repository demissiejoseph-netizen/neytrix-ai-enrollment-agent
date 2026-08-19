namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Loads FAQ/policy content into knowledge_chunks so answer_faq's vector search (GAP-04) has
/// something to retrieve. knowledge_chunks ships empty - this is not one of the 11 canonical
/// model-facing tools, just the load-time path an operator (or a seed script) uses to populate
/// a tenant's knowledge base.
/// </summary>
public interface IKnowledgeIngestionService
{
    /// <summary>
    /// Embeds <paramref name="content"/> with the RetrievalDocument task type and stores it as a
    /// new knowledge_chunks row. Callers are responsible for chunking long source documents into
    /// reasonably sized pieces before calling this - it does not split content itself.
    /// </summary>
    Task<Guid> IngestAsync(
        Guid tenantId,
        string sourceType,
        string content,
        string? sourceRef = null,
        string metadataJson = "{}",
        CancellationToken cancellationToken = default);
}
