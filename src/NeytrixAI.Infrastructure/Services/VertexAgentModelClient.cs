using Google.Cloud.AIPlatform.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Thin Vertex Gemini client. The application layer owns authorization and execution of tool calls.
/// </summary>
public sealed class VertexAgentModelClient : IAgentModelClient
{
    private readonly string _projectId;
    private readonly string _location;
    private readonly string _model;
    private readonly ILogger<VertexAgentModelClient> _logger;
    private PredictionServiceClient? _client;

    public VertexAgentModelClient(IConfiguration configuration, ILogger<VertexAgentModelClient> logger)
    {
        _projectId = configuration["VertexAI:ProjectId"] ?? throw new InvalidOperationException("VertexAI:ProjectId is required.");
        _location = configuration["VertexAI:Location"] ?? "us-central1";
        _model = configuration["VertexAI:Model"] ?? "gemini-1.5-flash";
        _logger = logger;
    }

    public async Task<AgentModelResponse> GenerateReplyAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        _client ??= new PredictionServiceClientBuilder
        {
            Endpoint = $"{_location}-aiplatform.googleapis.com"
        }.Build();

        var response = await _client.GenerateContentAsync(new GenerateContentRequest
        {
            Model = $"projects/{_projectId}/locations/{_location}/publishers/google/models/{_model}",
            Contents =
            {
                new Content
                {
                    Role = "user",
                    Parts = { new Part { Text = BuildPrompt(request) } }
                }
            },
            Tools =
            {
                new Tool
                {
                    FunctionDeclarations =
                    {
                        request.ToolDeclarations.Select(name => new FunctionDeclaration
                        {
                            Name = name,
                            Description = $"Invoke the application tool named {name}."
                        })
                    }
                }
            }
        }, cancellationToken);

        var candidate = response.Candidates.FirstOrDefault();
        var content = string.Concat(candidate?.Content?.Parts.Select(part => part.Text) ?? Enumerable.Empty<string>());
        if (string.IsNullOrWhiteSpace(content))
            content = "I couldn’t generate a response. A staff member can help you continue.";

        _logger.LogInformation("Vertex returned {CandidateCount} candidate(s) for session {SessionToken}", response.Candidates.Count, request.SessionToken);
        return new AgentModelResponse(content);
    }

    private static string BuildPrompt(AgentModelRequest request) =>
        $"""
        You are an enrollment assistant. Current state: {request.CurrentState}.
        User message: {request.UserMessage}
        Respond helpfully and do not claim a tool succeeded unless its application service returned a result.
        """;
}
