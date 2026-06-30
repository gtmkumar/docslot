using System.Runtime.CompilerServices;

namespace mediq.IntegrationTests;

/// <summary>
/// Assembly-wide test-host configuration applied before any <c>WebApplicationFactory&lt;Program&gt;</c> boots.
/// <para>
/// Disables the WhatsApp <c>OutboxDrainWorker</c> background loop for the WHOLE integration suite. The suite
/// runs ~8 factory hosts in parallel against a single local Postgres; an autonomous 5s-polling background
/// worker on every host adds connection churn that tips the already-tight pool into transient
/// <c>53300 remaining connection slots are reserved</c> / cascading-500 failures (see the integration-test-db
/// notes). No test depends on the background drain RUNNING — the outbound drain tests drive
/// <c>IOutboxDrainStore</c> + the sender explicitly, and the inbound tests assert ENQUEUE, not delivery. The
/// worker stays default-ON for the real app (Development); this only governs the test host.
/// </para>
/// The env var is read by <c>Program</c>'s default <c>AddEnvironmentVariables()</c> provider
/// (<c>WhatsApp__OutboxWorkerEnabled</c> ⇒ <c>WhatsApp:OutboxWorkerEnabled</c>).
/// </summary>
internal static class TestHostConfig
{
    [ModuleInitializer]
    internal static void DisableOutboxBackgroundWorker()
    {
        // JWT prod-guard exemption for the WHOLE suite. JwtSigningKeyGuard runs at host startup and, outside
        // Development, throws on the committed dev signing key — but the test factories all carry that dev key
        // and most boot in Production (default env). Setting Jwt:AllowDevSigningKey=true centrally lets every
        // test host accept the dev key regardless of environment, so the guard cannot fail their boot. The
        // dedicated prod-guard test (GatewayAuthEdgeTests) overrides this back to false (+ Production) per-host
        // via AddInMemoryCollection to assert the throw. (Jwt__AllowDevSigningKey ⇒ Jwt:AllowDevSigningKey.)
        Environment.SetEnvironmentVariable("Jwt__AllowDevSigningKey", "true");
        Environment.SetEnvironmentVariable("WhatsApp__OutboxWorkerEnabled", "false");
        // Same rationale for the booking maintenance worker: its startup slot-materialize races the
        // slot-generation tests (it would pre-create the very slots a test asserts it created) and adds
        // pool churn. Tests drive ISlotGenerationService / ISlotHoldService explicitly. Default-ON for the app.
        Environment.SetEnvironmentVariable("Booking__MaintenanceWorkerEnabled", "false");
        // Webhook delivery worker is off suite-wide: publish now only ENQUEUES (no in-request delivery), so a
        // seeded dead-URL subscription can never stall a booking POST. Delivery tests drive the drain store
        // explicitly. Default-ON for the app (Webhooks__DeliveryWorkerEnabled ⇒ Webhooks:DeliveryWorkerEnabled).
        Environment.SetEnvironmentVariable("Webhooks__DeliveryWorkerEnabled", "false");
        // Integration-event drain worker stays off suite-wide (it's already DEFAULT-OFF, but make it explicit so
        // no factory's config layering flips it on): the outbox tests drive IIntegrationEventOutboxDrainStore /
        // IIntegrationOutboxStore directly, so no autonomous poller adds pool churn across the ~8 parallel hosts.
        Environment.SetEnvironmentVariable("Messaging__DrainWorkerEnabled", "false");
        // Retention pruner worker is force-OFF suite-wide (already DEFAULT-OFF, made explicit): the prune tests
        // drive IRetentionPruneStore directly. A live sweep deleting AGED success rows off the SHARED hot outbox/
        // webhook tables under the parallel hosts would be both pool churn AND non-deterministic — never run it.
        Environment.SetEnvironmentVariable("Retention__PrunerEnabled", "false");
    }
}
