namespace NeytrixAI.Domain.Entities;

/// <summary>
/// Mirrors the <c>knowledge_chunks</c> table in db/migrations/001_initial_schema.sql - the
/// pgvector-backed store <c>answer_faq</c> searches for GAP-04. Kept free of any pgvector/Npgsql
/// dependency (Embedding is a plain float[]) so the Domain project's zero-external-package rule
/// holds; conversion to the provider-specific vector type happens only in
/// NeytrixAI.Infrastructure's repository implementation.
/// </summary>
public sealed class KnowledgeChunk
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string SourceType { get; private set; } = default!;
    public string? SourceRef { get; private set; }
    public string Content { get; private set; } = default!;
    public float[] Embedding { get; private set; } = default!;
    public string MetadataJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private KnowledgeChunk() { }

    public static KnowledgeChunk Create(
        Guid tenantId,
        string sourceType,
        string content,
        float[] embedding,
        string? sourceRef = null,
        string metadataJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (sourceType is not ("faq" or "policy" or "program" or "custom"))
            throw new ArgumentException("sourceType must be faq, policy, program, or custom.", nameof(sourceType));
        if (embedding is null || embedding.Length == 0)
            throw new ArgumentException("embedding must be a non-empty vector.", nameof(embedding));

        var now = DateTimeOffset.UtcNow;
        return new KnowledgeChunk
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SourceType = sourceType,
            SourceRef = sourceRef,
            Content = content,
            Embedding = embedding,
            MetadataJson = metadataJson,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
