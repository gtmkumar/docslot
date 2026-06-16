using mediq.Application.Cqrs;

namespace mediq.Application.Features.Docslot.WhatsApp;

/// <summary>
/// Processes ONE inbound WhatsApp message that has already cleared signature verification and tenant
/// resolution at the edge. The controller maps the Meta payload to this command per message and dispatches
/// it; the handler owns idempotency (processed_messages), the conversation state machine, and enqueuing the
/// next outbound prompt to the outbox.
/// <para>
/// Not <see cref="IRequireIdempotency"/>: idempotency here is keyed by the WhatsApp message id inside the
/// handler (the header-based pipeline idempotency is for client POSTs, not provider redeliveries).
/// </para>
/// </summary>
public sealed record ProcessInboundWhatsAppMessageCommand(
    Guid TenantId,
    string WhatsAppMessageId,
    string FromPhone,
    string MessageType,
    string? Body,
    string? SenderDisplayName) : ICommand<ProcessInboundResult>;

/// <summary>Outcome of processing — useful for tests/observability. Booking ids are null unless this turn confirmed.</summary>
public sealed record ProcessInboundResult(
    bool Skipped,
    string NewStep,
    string? OutboundText,
    Guid? BookingId,
    string? BookingNumber);
