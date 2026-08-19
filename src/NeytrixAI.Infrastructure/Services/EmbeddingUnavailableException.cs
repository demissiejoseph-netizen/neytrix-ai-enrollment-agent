namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Thrown by <see cref="NullEmbeddingService"/> when Vertex AI is not configured. Deliberately a
/// distinct exception type (not just returning a zero vector) so <c>answer_faq</c> can catch it
/// specifically and degrade to "get a human" - the same fail-closed pattern
/// <see cref="NullAgentModelClient"/> uses for the chat model. A silently-returned zero vector
/// would instead rank every knowledge chunk by an arbitrary tie-break and confidently hand back
/// wrong answers, which is worse than refusing.
/// </summary>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string message) : base(message) { }
}
