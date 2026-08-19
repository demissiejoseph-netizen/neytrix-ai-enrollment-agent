using System.Text.Json.Nodes;
using NeytrixAI.Infrastructure.Services;

namespace NeytrixAI.Api.Tools;

/// <summary>
/// Model-facing JSON Schemas for the 11 canonical tools in ToolContracts.cs, plus one
/// orchestration-only control function (set_stage). SessionToken is deliberately excluded
/// from every schema - the server injects it from the authenticated session, the model
/// never supplies it and could not be trusted to. set_stage is never dispatched to
/// IToolExecutionService; AgentOrchestrationService intercepts and validates it directly
/// against ConversationStateMachine before touching any business tool.
/// </summary>
public static class GeminiToolSchemas
{
    public const string SetStageFunctionName = "set_stage";

    public static IReadOnlyList<ModelToolDeclaration> All { get; } = BuildAll();

    private static IReadOnlyList<ModelToolDeclaration> BuildAll() => new List<ModelToolDeclaration>
    {
        new("answer_faq",
            "Answer a general question about programs, policies, or the club using the knowledge base. Use for FAQ-style questions that are not about a specific existing registration.",
            Schema(
                required: new[] { "question" },
                props: new Dictionary<string, JsonObject>
                {
                    ["question"] = Str("The guardian's question, verbatim or lightly cleaned up.")
                })),

        new("upsert_guardian",
            "Create or update the guardian (parent/caregiver) record. Only call this once you have their first name, last name, email, and an explicit yes/no answer to data-processing consent - never guess consent.",
            Schema(
                required: new[] { "first_name", "last_name", "email", "gdpr_consent_given" },
                props: new Dictionary<string, JsonObject>
                {
                    ["first_name"] = Str("Guardian's first name."),
                    ["last_name"] = Str("Guardian's last name."),
                    ["email"] = Str("Guardian's email address."),
                    ["phone"] = Str("Guardian's phone number, if they gave one."),
                    ["gdpr_consent_given"] = Bool("True only if the guardian explicitly agreed to data processing; false if they declined.")
                })),

        new("add_player",
            "Add a child/player under a guardian who has already given GDPR consent. Fails if consent has not been recorded for that guardian.",
            Schema(
                required: new[] { "guardian_id", "first_name", "last_name", "date_of_birth" },
                props: new Dictionary<string, JsonObject>
                {
                    ["guardian_id"] = Str("The GuardianId returned by upsert_guardian."),
                    ["first_name"] = Str("Player's first name."),
                    ["last_name"] = Str("Player's last name."),
                    ["date_of_birth"] = Str("Player's date of birth as YYYY-MM-DD."),
                    ["gender"] = Str("Player's gender, only if the guardian shares it and a program's rules need it.")
                })),

        new("match_programs",
            "Find programs a specific player is eligible for, ranked by relevance.",
            Schema(
                required: new[] { "player_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["player_id"] = Str("The PlayerId returned by add_player.")
                })),

        new("get_available_slots",
            "List open assessment slots for a program, optionally for a specific week.",
            Schema(
                required: new[] { "program_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["program_id"] = Str("The ProgramId the guardian is interested in."),
                    ["preferred_week_of"] = Str("Optional YYYY-MM-DD for the Monday of the desired week.")
                })),

        new("book_assessment",
            "Book a specific assessment slot for a registration.",
            Schema(
                required: new[] { "registration_id", "slot_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["registration_id"] = Str("The RegistrationId this assessment is for."),
                    ["slot_id"] = Str("The SlotId returned by get_available_slots.")
                })),

        new("send_waiver",
            "Send the liability waiver link for a registration.",
            Schema(
                required: new[] { "registration_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["registration_id"] = Str("The RegistrationId needing a waiver.")
                })),

        new("create_payment_link",
            "Create a Stripe checkout link for a registration's fee or deposit.",
            Schema(
                required: new[] { "registration_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["registration_id"] = Str("The RegistrationId to charge."),
                    ["deposit_only"] = Bool("True to charge only the deposit instead of the full price. Defaults to false.")
                })),

        new("create_registration",
            "Create a registration linking a guardian, player, and program. Enrolls or waitlists automatically based on capacity.",
            Schema(
                required: new[] { "guardian_id", "player_id", "program_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["guardian_id"] = Str("The GuardianId."),
                    ["player_id"] = Str("The PlayerId."),
                    ["program_id"] = Str("The ProgramId the player is registering for.")
                })),

        new("escalate_to_staff",
            "Hand the conversation off to a human staff member. ALWAYS use this immediately for any safeguarding, medical, or complaint concern, or whenever you are not confident you can safely continue.",
            Schema(
                required: new[] { "reason" },
                props: new Dictionary<string, JsonObject>
                {
                    ["reason"] = Str("A concise explanation of why staff are needed."),
                    ["category"] = StrEnum("The escalation category.", new[] { "General", "Safeguarding", "Financial", "Medical", "Complaint" })
                })),

        new("check_registration_status",
            "Check the current status of an existing registration (waiver, payment, enrollment, waitlist position).",
            Schema(
                required: new[] { "registration_id" },
                props: new Dictionary<string, JsonObject>
                {
                    ["registration_id"] = Str("The RegistrationId to check.")
                })),

        new(SetStageFunctionName,
            "Orchestration control only - never a business action. Call this whenever the conversation should move to a different stage, for example once the guardian has given their name, move to asking for their email. Only use one of the exact stage names you are currently told are allowed - anything else will be rejected.",
            Schema(
                required: new[] { "next_state" },
                props: new Dictionary<string, JsonObject>
                {
                    ["next_state"] = Str("The exact name of the next conversation stage, from the allowed list you were given."),
                    ["reason"] = Str("Optional short reason for the move.")
                })),
    };

    private static string Schema(string[] required, Dictionary<string, JsonObject> props)
    {
        var properties = new JsonObject();
        foreach (var (key, value) in props)
            properties[key] = value;

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
        };
        return schema.ToJsonString();
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject StrEnum(string description, string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray())
    };
}
