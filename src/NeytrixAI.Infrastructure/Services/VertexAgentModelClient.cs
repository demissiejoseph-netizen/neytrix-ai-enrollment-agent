using System.Text.Json.Nodes;
using Google.Cloud.AIPlatform.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Real multi-turn Gemini function-calling client. Converts the orchestrator's
/// role-agnostic <see cref="ModelHistoryTurn"/> history into Vertex <see cref="Content"/>,
/// converts <see cref="ModelToolDeclaration"/> JSON Schemas into <see cref="FunctionDeclaration"/>
/// (via a hand-written JSON Schema -> OpenApiSchema walk, since OpenApiSchema's own JSON
/// serialization uses uppercase enum names and cannot be built by parsing a normal JSON Schema
/// string with protobuf's JsonParser), and interprets the first candidate's first part as
/// either a function call to hand back to the orchestrator, or a final text reply.
///
/// Never claims a state change on its own final turns - <see cref="AgentModelTurn.RequestedState"/>
/// is left null on the normal happy path here; the real Vertex loop only changes conversation
/// state through the model explicitly calling the set_stage control function, which the
/// orchestrator intercepts. RequestedState is only set here as a fail-closed signal when the
/// Vertex call itself breaks (network/auth/parsing failure) - not a proactive state decision by
/// the model, but a safety net that degrades to "get a human" the same way NullAgentModelClient does.
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

    public async Task<AgentModelTurn> GetNextTurnAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        _client ??= new PredictionServiceClientBuilder
        {
            Endpoint = $"{_location}-aiplatform.googleapis.com"
        }.Build();

        var generateRequest = new GenerateContentRequest
        {
            Model = $"projects/{_projectId}/locations/{_location}/publishers/google/models/{_model}",
            SystemInstruction = BuildSystemInstruction(request)
        };
        generateRequest.Contents.AddRange(request.History.Select(ToContent));
        generateRequest.Tools.Add(new Tool
        {
            FunctionDeclarations = { request.Tools.Select(ToFunctionDeclaration) }
        });
        // ToolConfig.FunctionCallingConfig.Mode is left at its default (Auto) - we don't force
        // function calling on every turn; a plain conversational reply is a valid turn too.

        GenerateContentResponse response;
        try
        {
            response = await _client.GenerateContentAsync(generateRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Vertex GenerateContent call failed for session {SessionToken}", request.SessionToken);
            return FailClosed();
        }

        var parts = response.Candidates.FirstOrDefault()?.Content?.Parts;
        var functionCallPart = parts?.FirstOrDefault(p => p.FunctionCall is not null);
        if (functionCallPart?.FunctionCall is { } call)
        {
            var argsJson = JsonFormatter.Default.Format(call.Args ?? new Struct());
            return new AgentModelTurn(IsFunctionCall: true, FunctionCallName: call.Name, FunctionCallArgsJson: argsJson);
        }

        var text = string.Concat(parts?.Where(p => p.HasText).Select(p => p.Text) ?? Enumerable.Empty<string>());
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Vertex returned neither a function call nor text for session {SessionToken}", request.SessionToken);
            return FailClosed();
        }

        return new AgentModelTurn(IsFunctionCall: false, FinalText: text);
    }

    private static AgentModelTurn FailClosed() => new(
        IsFunctionCall: false,
        FinalText: "I'm having trouble continuing right now. A staff member can help you from here.",
        RequestedState: "EscalatedToStaff");

    private static Content BuildSystemInstruction(AgentModelRequest request)
    {
        var allowed = request.AllowedNextStates.Count > 0
            ? string.Join(", ", request.AllowedNextStates)
            : "(none - this conversation is in a terminal state)";

        var text = $"""
            You are Neytrix, a youth sports enrollment assistant. Be warm, concise, and safe.

            Current conversation stage: {request.CurrentState}.
            Allowed next stages: {allowed}.
            Call the set_stage function with exactly one of the allowed stage names above when you are ready to move
            the conversation forward. Do not call set_stage just to restate the current stage, and never name a stage
            that is not in that list - it will be rejected.

            Never claim a booking was made, a payment link was created, a waiver was sent, or a registration exists
            unless the matching tool's function response actually confirms it. If a tool call fails, tell the guardian
            honestly and either retry with corrected details or escalate.

            Call escalate_to_staff immediately for any safeguarding, medical, or complaint concern, or whenever you are
            not confident you can continue safely - do not attempt to handle those situations yourself.
            """;

        return new Content { Parts = { new Part { Text = text } } };
    }

    private static Content ToContent(ModelHistoryTurn turn) => turn.Role switch
    {
        ModelTurnRole.User => new Content
        {
            Role = "user",
            Parts = { new Part { Text = turn.Text ?? string.Empty } }
        },
        ModelTurnRole.Model when turn.FunctionCallName is not null => new Content
        {
            Role = "model",
            Parts =
            {
                new Part
                {
                    FunctionCall = new FunctionCall
                    {
                        Name = turn.FunctionCallName,
                        Args = ParseStruct(turn.FunctionCallArgsJson)
                    }
                }
            }
        },
        ModelTurnRole.Model => new Content
        {
            Role = "model",
            Parts = { new Part { Text = turn.Text ?? string.Empty } }
        },
        ModelTurnRole.Function => new Content
        {
            Role = "function",
            Parts =
            {
                new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Name = turn.FunctionResponseName ?? string.Empty,
                        Response = ParseStruct(turn.FunctionResponseJson)
                    }
                }
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.Role, "Unknown ModelTurnRole.")
    };

    private static Struct ParseStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Struct();
        try
        {
            return JsonParser.Default.Parse<Struct>(json);
        }
        catch (InvalidProtocolBufferException)
        {
            // A malformed args/response payload should never crash the turn - fall back to an
            // empty struct and let the model/orchestrator react to missing fields normally.
            return new Struct();
        }
    }

    private static FunctionDeclaration ToFunctionDeclaration(ModelToolDeclaration tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        Parameters = ConvertJsonSchema(JsonNode.Parse(tool.JsonSchema) as JsonObject ?? new JsonObject())
    };

    /// <summary>
    /// Hand-written recursive JSON Schema -> OpenApiSchema converter. OpenApiSchema is a
    /// regular protobuf message whose own JSON format uses uppercase type names
    /// ("OBJECT"/"STRING"/...), so it cannot be built by feeding a standard lowercase JSON
    /// Schema string straight into protobuf's JsonParser - each field must be copied by hand.
    /// </summary>
    private static OpenApiSchema ConvertJsonSchema(JsonObject node)
    {
        var schema = new OpenApiSchema();

        if (TryGetString(node, "type", out var typeStr))
        {
            schema.Type = typeStr!.ToLowerInvariant() switch
            {
                "string" => Google.Cloud.AIPlatform.V1.Type.String,
                "number" => Google.Cloud.AIPlatform.V1.Type.Number,
                "integer" => Google.Cloud.AIPlatform.V1.Type.Integer,
                "boolean" => Google.Cloud.AIPlatform.V1.Type.Boolean,
                "array" => Google.Cloud.AIPlatform.V1.Type.Array,
                "object" => Google.Cloud.AIPlatform.V1.Type.Object,
                _ => Google.Cloud.AIPlatform.V1.Type.Unspecified
            };
        }

        if (TryGetString(node, "description", out var description))
            schema.Description = description;

        if (node.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumArr)
            foreach (var entry in enumArr)
                if (entry is JsonValue ev && ev.TryGetValue(out string? es))
                    schema.Enum.Add(es);

        if (node.TryGetPropertyValue("required", out var requiredNode) && requiredNode is JsonArray requiredArr)
            foreach (var entry in requiredArr)
                if (entry is JsonValue rv && rv.TryGetValue(out string? rs))
                    schema.Required.Add(rs);

        if (node.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject propsObj)
            foreach (var (key, value) in propsObj)
                if (value is JsonObject valueObj)
                    schema.Properties[key] = ConvertJsonSchema(valueObj);

        if (node.TryGetPropertyValue("items", out var itemsNode) && itemsNode is JsonObject itemsObj)
            schema.Items = ConvertJsonSchema(itemsObj);

        return schema;
    }

    private static bool TryGetString(JsonObject node, string key, out string? value)
    {
        value = null;
        if (node.TryGetPropertyValue(key, out var raw) && raw is JsonValue jv && jv.TryGetValue(out string? s))
        {
            value = s;
            return true;
        }
        return false;
    }
}
