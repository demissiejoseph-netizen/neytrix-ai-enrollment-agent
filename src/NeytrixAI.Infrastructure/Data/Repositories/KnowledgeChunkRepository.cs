using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;
using Pgvector;

namespace NeytrixAI.Infrastructure.Data.Repositories;

/// <summary>
/// GAP-04: real pgvector-backed retrieval for knowledge_chunks, replacing the ILIKE keyword
/// search that used to live inline in ToolExecutionService.AnswerFaqAsync. Ranks by cosine
/// distance (the <c>&lt;=&gt;</c> operator) to match the ivfflat vector_cosine_ops index the
/// migration already declares on this table.
/// </summary>
public sealed class KnowledgeChunkRepository : IKnowledgeChunkRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public KnowledgeChunkRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Guid> CreateAsync(KnowledgeChunk chunk, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(chunk.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO knowledge_chunks (id, tenant_id, source_type, source_ref, content, embedding, metadata, created_at, updated_at)
            VALUES (@Id, @TenantId, @SourceType, @SourceRef, @Content, @Embedding, CAST(@MetadataJson AS jsonb), @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            chunk.Id,
            chunk.TenantId,
            chunk.SourceType,
            chunk.SourceRef,
            chunk.Content,
            Embedding = new Vector(chunk.Embedding),
            chunk.MetadataJson,
            chunk.CreatedAt,
            chunk.UpdatedAt
        }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<KnowledgeChunkMatch>> SearchAsync(
        Guid tenantId, float[] queryEmbedding, IReadOnlyList<string> sourceTypes, int limit, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = """
            SELECT id, content, embedding <=> @QueryEmbedding AS distance
            FROM knowledge_chunks
            WHERE tenant_id = @TenantId
              AND source_type = ANY(@SourceTypes)
            ORDER BY embedding <=> @QueryEmbedding
            LIMIT @Limit;
            """;
        var rows = await connection.QueryAsync<KnowledgeChunkMatch>(new CommandDefinition(sql, new
        {
            TenantId = tenantId,
            QueryEmbedding = new Vector(queryEmbedding),
            SourceTypes = sourceTypes.ToArray(),
            Limit = limit
        }, cancellationToken: cancellationToken));
        return rows.ToList();
    }
}
