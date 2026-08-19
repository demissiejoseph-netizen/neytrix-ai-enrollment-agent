using System.Text.Json;
using NeytrixAI.Infrastructure.Services;

namespace NeytrixAI.Tests.Integration;

/// <summary>
/// A deterministic, scripted stand-in for the real Vertex/Gemini model client. It plays back a
/// fixed sequence of function calls and final replies that mimic a real guardian walking
/// through the full enrollment conversation - guardian intake, player intake, program
/// matching, registration, assessment booking, waiver, and payment link - driving the actual
/// <c>ConversationStateMachine</c> transition graph exactly as the real model would via
/// <c>set_stage</c> calls.
///
/// The script spans multiple separate <c>ProcessMessageAsync</c> calls (i.e. multiple simulated
/// user messages), matching how a real conversation actually happens: every simulated user
/// message resets the orchestrator's 8-iteration loop budget and calls
/// <see cref="GetNextTurnAsync"/> repeatedly until this client returns a non-function-call
/// (final text) turn. Because this instance is reused across those calls, a single monotonic
/// step counter is sufficient - there's no need to reset anything between simulated messages.
///
/// Dynamic ids that the real tool layer generates (guardian_id, player_id, registration_id,
/// slot_id) are not known ahead of time, so instead of hardcoding them the script inspects the
/// full persisted conversation history handed back in <see cref="AgentModelRequest.History"/>
/// on every call - exactly as a real LLM would read prior tool results - and extracts them from
/// the relevant function response.
/// </summary>
public sealed class ScriptedAgentModelClient : IAgentModelClient
{
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly Guid _programId;
    private readonly List<Func<AgentModelRequest, AgentModelTurn>> _script;
    private int _step;

    public ScriptedAgentModelClient(Guid programId)
    {
        _programId = programId;
        _script = BuildScript();
    }

    public Task<AgentModelTurn> GetNextTurnAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        if (_step >= _script.Count)
            throw new InvalidOperationException("ScriptedAgentModelClient ran out of scripted turns - the conversation advanced further than expected.");

        var turn = _script[_step](request);
        _step++;
        return Task.FromResult(turn);
    }

    /// <summary>True once every scripted turn has been consumed - lets the test assert the whole script ran, not just the first few steps.</summary>
    public bool IsScriptExhausted => _step >= _script.Count;

    private List<Func<AgentModelRequest, AgentModelTurn>> BuildScript() => new()
    {
        // --- simulated user message 1: "Hi, I'd like to enroll my son." ---
        _ => SetStage("CollectingGuardianName"),
        _ => SetStage("CollectingGuardianEmail"),
        _ => SetStage("CollectingGuardianPhone"),
        _ => FinalText("Great - and what's the best phone number to reach you at?"),

        // --- simulated user message 2: "206-555-1234" ---
        _ => SetStage("CollectingGdprConsent"),
        _ => FinalText("Before I save your details, do you consent to our data & privacy policy?"),

        // --- simulated user message 3: "Yes, I consent. Alex Rivera, alex.rivera.e2e@example.com" ---
        _ => FunctionCall("upsert_guardian", JsonSerializer.Serialize(new
        {
            first_name = "Alex",
            last_name = "Rivera",
            email = "alex.rivera.e2e@example.com",
            phone = "+12065551234",
            gdpr_consent_given = true
        }, SnakeCase)),
        _ => SetStage("CollectingPlayerName"),
        _ => FinalText("Thanks, Alex! What's your child's first and last name?"),

        // --- simulated user message 4: "Sam Rivera, born 2016-04-12, male" ---
        _ => SetStage("CollectingPlayerDob"),
        _ => SetStage("CollectingPlayerGender"),
        req => FunctionCall("add_player", JsonSerializer.Serialize(new
        {
            guardian_id = ExtractGuid(req.History, "upsert_guardian", "guardian_id"),
            first_name = "Sam",
            last_name = "Rivera",
            date_of_birth = "2016-04-12",
            gender = "male"
        }, SnakeCase)),
        _ => SetStage("ShowingProgramMatches"),
        req => FunctionCall("match_programs", JsonSerializer.Serialize(new
        {
            player_id = ExtractGuid(req.History, "add_player", "player_id")
        }, SnakeCase)),
        _ => FinalText("Sam is eligible for our Youth Soccer Fundamentals program. Want to go ahead and register?"),

        // --- simulated user message 5: "Yes, please register Sam." ---
        req => FunctionCall("create_registration", JsonSerializer.Serialize(new
        {
            guardian_id = ExtractGuid(req.History, "upsert_guardian", "guardian_id"),
            player_id = ExtractGuid(req.History, "add_player", "player_id"),
            program_id = _programId
        }, SnakeCase)),
        _ => SetStage("ProposingAssessmentSlots"),
        _ => FunctionCall("get_available_slots", JsonSerializer.Serialize(new { program_id = _programId }, SnakeCase)),
        _ => FinalText("Here are two assessment times next week - which works better for you?"),

        // --- simulated user message 6: "The first one works." ---
        _ => SetStage("ConfirmingAssessmentBooking"),
        req => FunctionCall("book_assessment", JsonSerializer.Serialize(new
        {
            registration_id = ExtractGuid(req.History, "create_registration", "registration_id"),
            slot_id = ExtractFirstSlotId(req.History)
        }, SnakeCase)),
        _ => SetStage("AssessmentBooked"),
        _ => SetStage("SendingWaiver"),
        req => FunctionCall("send_waiver", JsonSerializer.Serialize(new
        {
            registration_id = ExtractGuid(req.History, "create_registration", "registration_id")
        }, SnakeCase)),
        _ => SetStage("WaiverPending"),
        _ => FinalText("Assessment booked, and the waiver link is on its way to your email. Let me know once it's signed."),

        // --- simulated user message 7: "Signed! What's next for payment?" ---
        _ => SetStage("SendingPaymentLink"),
        req => FunctionCall("create_payment_link", JsonSerializer.Serialize(new
        {
            registration_id = ExtractGuid(req.History, "create_registration", "registration_id"),
            deposit_only = false
        }, SnakeCase)),
        _ => SetStage("PaymentPending"),
        _ => FinalText("Here's your payment link - once it clears, Sam will be fully enrolled!"),
    };

    private static AgentModelTurn FunctionCall(string name, string argsJson) =>
        new(IsFunctionCall: true, FunctionCallName: name, FunctionCallArgsJson: argsJson);

    private static AgentModelTurn SetStage(string nextState) =>
        FunctionCall("set_stage", JsonSerializer.Serialize(new { next_state = nextState }, SnakeCase));

    private static AgentModelTurn FinalText(string text) =>
        new(IsFunctionCall: false, FinalText: text);

    /// <summary>Scans conversation history backward for the most recent response from <paramref name="functionName"/> and reads a Guid property out of it.</summary>
    private static Guid ExtractGuid(IReadOnlyList<ModelHistoryTurn> history, string functionName, string propertyName)
    {
        using var response = ExtractResponse(history, functionName);
        if (!response.RootElement.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidOperationException(
                $"'{functionName}' response had no '{propertyName}' property. Raw response: {response.RootElement.GetRawText()}");
        }
        return element.GetGuid();
    }

    private static string ExtractFirstSlotId(IReadOnlyList<ModelHistoryTurn> history)
    {
        using var response = ExtractResponse(history, "get_available_slots");
        return response.RootElement.GetProperty("slots")[0].GetProperty("slot_id").GetString()
               ?? throw new InvalidOperationException("get_available_slots response had no slot_id.");
    }

    private static JsonDocument ExtractResponse(IReadOnlyList<ModelHistoryTurn> history, string functionName)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var turn = history[i];
            if (turn.Role == ModelTurnRole.Function && turn.FunctionResponseName == functionName && turn.FunctionResponseJson is not null)
                return JsonDocument.Parse(turn.FunctionResponseJson);
        }

        throw new InvalidOperationException($"Expected a prior '{functionName}' function response in conversation history but found none.");
    }
}
