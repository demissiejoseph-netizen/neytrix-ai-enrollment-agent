using Google.Cloud.AIPlatform.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Struct = Google.Protobuf.WellKnownTypes.Struct;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Real embeddings for GAP-04's RAG path, via Vertex AI's text embeddings Predict API
/// (<c>gemini-embedding-001</c>) - a different Vertex surface from <see cref="VertexAgentModelClient"/>'s
/// GenerateContent chat calls, but the same project/location configuration and the same
/// PredictionServiceClient class.
///
/// Two model-specific constraints drive the shape of this class:
///  - <c>gemini-embedding-001</c> accepts exactly one instance per Predict request (unlike the
///    older gecko embedding models, which allowed batching); <see cref="EmbedBatchAsync"/> loops
///    one call per text rather than sending them together.
///  - The model's native output is 3072-dimensional, but supports an <c>outputDimensionality</c>
///    request parameter that truncates-and-renormalizes to a smaller size. This must match
///    <c>knowledge_chunks.embedding</c>'s declared <c>vector(1536)</c> width exactly - a mismatch
///    fails loudly at the database with a dimension error rather than silently corrupting
///    similarity rankings, so <see cref="_dimensions"/> defaults to 1536 to line up with the
///    schema and is configurable only in case the schema's column width ever changes too.
/// </summary>
public sealed class VertexEmbeddingService : IEmbeddingService
{
    private readonly string _projectId;
    private readonly string _location;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly ILogger<VertexEmbeddingService> _logger;
    private PredictionServiceClient? _client;

    public VertexEmbeddingService(IConfiguration configuration, ILogger<VertexEmbeddingService> logger)
    {
        _projectId = configuration["VertexAI:ProjectId"] ?? throw new InvalidOperationException("VertexAI:ProjectId is required.");
        _location = configuration["VertexAI:Location"] ?? "us-central1";
        _model = configuration["VertexAI:EmbeddingModel"] ?? "gemini-embedding-001";
        _dimensions = int.TryParse(configuration["VertexAI:EmbeddingDimensions"], out var dims) ? dims : 1536;
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string text, EmbeddingTaskType taskType, CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchAsync(new[] { text }, taskType, cancellationToken);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, EmbeddingTaskType taskType, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        _client ??= new PredictionServiceClientBuilder
        {
            Endpoint = $"{_location}-aiplatform.googleapis.com"
        }.Build();

        var endpoint = $"projects/{_projectId}/locations/{_location}/publishers/google/models/{_model}";
        var taskTypeString = taskType == EmbeddingTaskType.RetrievalQuery ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT";
        var parameters = BuildParameters(_dimensions);

        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // gemini-embedding-001 only accepts one instance per Predict call - see class doc.
            var instance = BuildInstance(text, taskTypeString);
            PredictResponse response;
            try
            {
                response = await _client.PredictAsync(endpoint, new[] { instance }, parameters, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Vertex embeddings Predict call failed for model {Model}", _model);
                throw new EmbeddingUnavailableException("Embedding request to Vertex AI failed.");
            }

            var prediction = response.Predictions.FirstOrDefault();
            var values = prediction?.StructValue?.Fields.GetValueOrDefault("embeddings")?.StructValue?.Fields.GetValueOrDefault("values")?.ListValue?.Values;
            if (values is null || values.Count == 0)
            {
                _logger.LogError("Vertex embeddings Predict call for model {Model} returned no embedding values", _model);
                throw new EmbeddingUnavailableException("Embedding response from Vertex AI had no values.");
            }

            var vector = new float[values.Count];
            for (var i = 0; i < values.Count; i++)
                vector[i] = (float)values[i].NumberValue;

            if (vector.Length != _dimensions)
            {
                _logger.LogError(
                    "Vertex embeddings Predict call for model {Model} returned {Actual} dimensions, expected {Expected}",
                    _model, vector.Length, _dimensions);
                throw new EmbeddingUnavailableException(
                    $"Embedding dimension mismatch: got {vector.Length}, expected {_dimensions}.");
            }

            vectors.Add(vector);
        }

        return vectors;
    }

    private static Value BuildInstance(string content, string taskType)
    {
        var s = new Struct();
        s.Fields["content"] = Value.ForString(content);
        s.Fields["task_type"] = Value.ForString(taskType);
        return Value.ForStruct(s);
    }

    private static Value BuildParameters(int outputDimensionality)
    {
        var s = new Struct();
        s.Fields["outputDimensionality"] = Value.ForNumber(outputDimensionality);
        s.Fields["autoTruncate"] = Value.ForBool(true);
        return Value.ForStruct(s);
    }
}
