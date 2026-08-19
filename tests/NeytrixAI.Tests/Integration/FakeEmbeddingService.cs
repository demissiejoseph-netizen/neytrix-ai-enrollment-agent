using System.Text.RegularExpressions;
using NeytrixAI.Infrastructure.Services;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// Deterministic embedding test double - never calls Vertex AI. Produces a 1536-dimensional
/// (matching knowledge_chunks.embedding's declared width) hashed bag-of-words vector: each word
/// increments a hash-selected dimension, then the vector is L2-normalized. This is not a real
/// semantic embedding, but within one test process run (string.GetHashCode() is stable for the
/// process lifetime, only randomized across separate runs) it gives text sharing vocabulary a
/// smaller cosine distance than unrelated text - enough to exercise the real pgvector ranking
/// path (KnowledgeChunkRepository.SearchAsync's ORDER BY embedding &lt;=&gt; @QueryEmbedding) end
/// to end without needing live Vertex AI credentials in CI or this sandbox.
/// </summary>
public sealed class FakeEmbeddingService : IEmbeddingService
{
    public const int Dimensions = 1536;

    private static readonly Regex WordPattern = new(@"[a-z0-9]+", RegexOptions.Compiled);

    public Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken cancellationToken = default) =>
        Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingTaskType taskType, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (Match match in WordPattern.Matches(text.ToLowerInvariant()))
        {
            var index = (int)((uint)match.Value.GetHashCode() % Dimensions);
            vector[index] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0f)
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;

        return vector;
    }
}
