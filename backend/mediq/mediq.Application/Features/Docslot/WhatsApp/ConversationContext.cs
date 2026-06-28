using System.Text.Json;
using System.Text.Json.Serialization;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// The conversational step names persisted in <c>docslot.conversations.current_step</c> (free-text varchar).
/// </summary>
public static class ConversationSteps
{
    public const string WhoFor = "who_for";
    public const string ChooseRelation = "choose_relation";
    public const string AskPatientPhone = "ask_patient_phone";
    public const string ChooseDepartment = "choose_department";
    public const string ChooseDoctor = "choose_doctor";
    public const string ChooseSlot = "choose_slot";
    public const string Confirm = "confirm";
    public const string Done = "done";
}

/// <summary>
/// Working state carried across turns in <c>docslot.conversations.context</c> (jsonb). Holds the user's
/// selections plus the numbered option lists last shown, so a numeric reply ("2") can be mapped back to the
/// concrete id without re-querying in a different order.
/// </summary>
public sealed record ConversationContext
{
    [JsonPropertyName("relation")] public string? Relation { get; init; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    /// <summary>For a behalf booking: the PATIENT's WhatsApp number (the booker is the conversation phone).</summary>
    [JsonPropertyName("patientPhone")] public string? PatientPhone { get; init; }

    [JsonPropertyName("departmentId")] public Guid? DepartmentId { get; init; }
    [JsonPropertyName("departmentName")] public string? DepartmentName { get; init; }
    [JsonPropertyName("doctorId")] public Guid? DoctorId { get; init; }
    [JsonPropertyName("doctorName")] public string? DoctorName { get; init; }
    [JsonPropertyName("slotId")] public Guid? SlotId { get; init; }
    [JsonPropertyName("slotLabel")] public string? SlotLabel { get; init; }

    /// <summary>Referral attribution carried from a /ref/{code} click → prefilled WhatsApp message: the link the
    /// patient arrived through + the broker who earns. Set when the code is detected; consumed at booking confirm.</summary>
    [JsonPropertyName("referralLinkId")] public Guid? ReferralLinkId { get; init; }
    [JsonPropertyName("referralBrokerId")] public Guid? ReferralBrokerId { get; init; }

    /// <summary>Option lists last presented (index → id), so a numbered reply resolves deterministically.</summary>
    [JsonPropertyName("departmentOptions")] public List<OptionEntry> DepartmentOptions { get; init; } = [];
    [JsonPropertyName("doctorOptions")] public List<OptionEntry> DoctorOptions { get; init; } = [];
    [JsonPropertyName("slotOptions")] public List<OptionEntry> SlotOptions { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ConversationContext FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ConversationContext()
            : JsonSerializer.Deserialize<ConversationContext>(json, Json) ?? new ConversationContext();
}

/// <summary>A single numbered option: the 1-based number shown, its id, and a human label.</summary>
public sealed record OptionEntry(
    [property: JsonPropertyName("n")] int Number,
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("label")] string Label);
