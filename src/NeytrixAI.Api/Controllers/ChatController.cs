using Microsoft.AspNetCore.Mvc;
using NeytrixAI.Api.Services;
using NeytrixAI.Api.Tools;

namespace NeytrixAI.Api.Controllers;

[ApiController]
[Route("api/v1/chat")]
public sealed class ChatController : ControllerBase
{
    private readonly IAgentOrchestrationService _orchestration;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IAgentOrchestrationService orchestration,
        ILogger<ChatController> logger)
    {
        _orchestration = orchestration;
        _logger = logger;
    }

    /// <summary>Start a new conversation session for this tenant's widget.</summary>
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(StartSessionResponse), 201)]
    public async Task<IActionResult> StartSession(
        [FromBody] StartSessionRequest request,
        CancellationToken ct)
    {
        var response = await _orchestration.StartSessionAsync(request, ct);
        return CreatedAtAction(nameof(GetSession), new { sessionToken = response.SessionToken }, response);
    }

    /// <summary>Get current session state.</summary>
    [HttpGet("sessions/{sessionToken}")]
    [ProducesResponseType(typeof(SessionStateResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetSession(
        [FromRoute] string sessionToken,
        CancellationToken ct)
    {
        var response = await _orchestration.GetSessionStateAsync(sessionToken, ct);
        return response is null ? NotFound() : Ok(response);
    }

    /// <summary>Send a user message and get the agent's reply.</summary>
    [HttpPost("sessions/{sessionToken}/messages")]
    [ProducesResponseType(typeof(ChatMessageResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> SendMessage(
        [FromRoute] string sessionToken,
        [FromBody] SendMessageRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Message content cannot be empty.");

        var response = await _orchestration.ProcessMessageAsync(sessionToken, request, ct);
        return response is null ? NotFound() : Ok(response);
    }

    /// <summary>Webhook for Stripe payment events.</summary>
    [HttpPost("webhooks/stripe")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> StripeWebhook(CancellationToken ct)
    {
        var payload = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        var signature = Request.Headers["Stripe-Signature"].ToString();
        await _orchestration.HandleStripeWebhookAsync(payload, signature, ct);
        return Ok();
    }
}

// DTOs for the Chat controller
public sealed record StartSessionRequest(
    string? GuardianEmail,
    string Channel = "widget");

public sealed record StartSessionResponse(
    string SessionToken,
    string GreetingMessage,
    string CurrentState);

public sealed record SessionStateResponse(
    string SessionToken,
    string CurrentState,
    bool IsEnded,
    Guid? GuardianId,
    Guid? PlayerId,
    Guid? RegistrationId);

public sealed record SendMessageRequest(
    string Content);

public sealed record ChatMessageResponse(
    string Role,
    string Content,
    string NewState,
    bool RequiresEscalation,
    string[] SuggestedActions);
