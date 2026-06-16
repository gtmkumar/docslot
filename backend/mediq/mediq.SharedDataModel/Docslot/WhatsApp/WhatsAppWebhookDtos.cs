using System.Text.Json.Serialization;

namespace mediq.SharedDataModel.Docslot.WhatsApp;

/// <summary>
/// Minimal, tolerant projection of the Meta WhatsApp Cloud API inbound webhook envelope. Only the fields
/// this flow needs are modelled; unknown fields are ignored. Deserialized from the EXACT raw body AFTER the
/// HMAC signature has been verified.
/// </summary>
public sealed record WhatsAppWebhookEnvelope(
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("entry")] IReadOnlyList<WhatsAppEntry>? Entry);

public sealed record WhatsAppEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("changes")] IReadOnlyList<WhatsAppChange>? Changes);

public sealed record WhatsAppChange(
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("value")] WhatsAppChangeValue? Value);

public sealed record WhatsAppChangeValue(
    [property: JsonPropertyName("messaging_product")] string? MessagingProduct,
    [property: JsonPropertyName("metadata")] WhatsAppMetadata? Metadata,
    [property: JsonPropertyName("contacts")] IReadOnlyList<WhatsAppContact>? Contacts,
    [property: JsonPropertyName("messages")] IReadOnlyList<WhatsAppMessage>? Messages);

public sealed record WhatsAppMetadata(
    [property: JsonPropertyName("display_phone_number")] string? DisplayPhoneNumber,
    [property: JsonPropertyName("phone_number_id")] string? PhoneNumberId);

public sealed record WhatsAppContact(
    [property: JsonPropertyName("wa_id")] string? WaId,
    [property: JsonPropertyName("profile")] WhatsAppProfile? Profile);

public sealed record WhatsAppProfile(
    [property: JsonPropertyName("name")] string? Name);

public sealed record WhatsAppMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] WhatsAppText? Text,
    [property: JsonPropertyName("interactive")] WhatsAppInteractive? Interactive);

public sealed record WhatsAppText(
    [property: JsonPropertyName("body")] string? Body);

/// <summary>Interactive replies (list/button). We read the selected id/title as the user's "reply text".</summary>
public sealed record WhatsAppInteractive(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("button_reply")] WhatsAppInteractiveReply? ButtonReply,
    [property: JsonPropertyName("list_reply")] WhatsAppInteractiveReply? ListReply);

public sealed record WhatsAppInteractiveReply(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title);
