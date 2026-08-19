using System.Text.Json;

namespace NeytrixAI.Api.Services;

/// <summary>
/// Shared JSON conventions for everything crossing the model boundary: tool arguments coming
/// in from Gemini function calls and tool results going back out as function responses.
/// snake_case matches the property names declared in GeminiToolSchemas.
/// </summary>
internal static class ToolJsonOptions
{
    public static readonly JsonSerializerOptions Model = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
