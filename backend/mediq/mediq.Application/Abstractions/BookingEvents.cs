namespace mediq.Application.Abstractions;

/// <summary>
/// Translates booking lifecycle transitions into platform integration events and publishes them through
/// the slice-02 <see cref="IWebhookPublisher"/> (sign → deliver → retry → outbox). Domain events stay
/// inside the service; this is the Application-boundary translation to a cross-service integration event.
/// Event types: <c>docslot.booking.created|confirmed|cancelled|completed|no_show</c>.
/// </summary>
public interface IBookingEventPublisher
{
    Task PublishAsync(string eventType, Guid tenantId, Guid bookingId, string? bookingNumber, object payload, CancellationToken ct);
}

/// <summary>Canonical docslot booking event-type tokens (registered in <c>platform_api.api_event_types</c>).</summary>
public static class BookingEventTypes
{
    public const string Created = "docslot.booking.created";
    public const string Confirmed = "docslot.booking.approved";   // schema seeds 'approved'; we map confirmed→approved
    public const string Cancelled = "docslot.booking.cancelled";
    public const string Completed = "docslot.booking.completed";
    public const string NoShow = "docslot.booking.no_show";
}

/// <summary>
/// Publishes commission lifecycle integration events through the slice-02 webhook pipeline. Payloads carry
/// IDs + amounts ONLY — NEVER patient PHI (DPDP; brokers see minimal patient data, and webhooks carry none).
/// Event types: <c>commission.attribution.created</c>, <c>commission.commission.earned</c>, <c>commission.payout.paid</c>.
/// </summary>
public interface IBrokerEventPublisher
{
    Task PublishAsync(string eventType, Guid tenantId, object payload, CancellationToken ct);
}
