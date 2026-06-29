---
name: project_slice_phase4_integration_outbox
description: Phase-4 durable integration-event outbox + RabbitMQ publisher seam (flagged, broker/consumer deferred); closes the lost-event gap in WebhookPublisher.
metadata:
  type: project
---

Phase-4 slice on branch `feat/phase4-integration-event-outbox`: a durable transactional integration-event
outbox + a flagged RabbitMQ publisher seam (honest-stub: dev/test default no-op, broker + consumer DEFERRED).

**Why:** Integration events were translated at the Application boundary and published ONLY through the webhook
pipeline (`WebhookPublisher.PublishAsync` → `platform_api.webhook_deliveries`, keyed to HTTP subscriptions). An
event with NO matching subscription was silently DISCARDED (data-loss bug). The outbox captures EVERY event
atomically with the business write.

**How to apply / key facts:**
- New table `platform_api.integration_event_outbox` (TABLE 27, after the api_event_types seed in
  `02_platform_api.sql`; bundle regenerated). Mirrors `webhook_deliveries` status machine
  (pending/processing/success/failed/abandoned) but NO webhook_id / NO subscription join — one row per event,
  fanned to the broker. NO RLS (follows the platform_api PaaS convention — same as webhook_deliveries; drain
  runs as plain docslot_app, no SECURITY DEFINER). Compensating controls: capture inside the command's
  tenant-scoped UoW tx; tenant_id for forensics only; payload IDs-only/no-PHI (COMMENT documents this);
  append + state-transition only (NO DELETE grant — no pruner this slice).
- The blanket `GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA platform_api TO docslot_app` (file 10, runs
  LAST) covers it on fresh DBs. The LIVE `docslot_platform` DB was already provisioned, so the table was applied
  idempotently (CREATE TABLE/INDEX IF NOT EXISTS) + an explicit re-GRANT to docslot_app.
- Tap: `WebhookPublisher` got an `IIntegrationOutboxStore outbox` ctor dep; `RecordAsync(evt)` is the FIRST
  statement of `PublishAsync` (raw INSERT … ON CONFLICT (event_id) DO NOTHING on the ambient PlatformDbContext
  connection → atomic with the business write; idempotent dedup). The subscription fan-out below is unchanged.
- Seam (mediq.Application/Abstractions/MessagingAbstractions.cs + Options/MessagingOptions.cs): `IIntegrationEventBus`
  (Null vs RabbitMq, Provider switch in InfrastructureRegistration mirroring AiService/Abdm), `IIntegrationOutboxStore`
  (capture), `IIntegrationEventOutboxDrainStore` (ClaimDue/MarkPublished/MarkFailed). Drain store mirrors
  WebhookDeliveryDrainStore: FOR UPDATE SKIP LOCKED + lease on next_retry_at; single-winner marks gated on
  `status='processing'`.
- `MessagingOptions` (SectionName="Messaging"): Provider="none" default, ExchangeName="docslot.events",
  ExchangeType="topic", DrainWorkerEnabled=FALSE default, MaxRetries=8, backoff/lease like the webhook worker.
- `IntegrationEventDrainWorker` (mediq.Api/Workers) registered behind `Messaging:DrainWorkerEnabled` (default
  false) in Program.cs; when Provider=rabbitmq, Program also calls `builder.AddRabbitMQClient("rabbitmq")`.
- AppHost: `builder.AddRabbitMQ("rabbitmq")` + `.WithReference(rabbit)` on mediq-api. Api keeps Provider=none so
  the AppHost boots WITHOUT a running broker. AI sibling AddPythonApp stays commented (consumer deferred).
- **Package deltas:** `Aspire.RabbitMQ.Client` 13.4.3 (Infrastructure + Api), `Aspire.Hosting.RabbitMQ` 13.4.3
  (AppHost). This forced `Polly.Core` 8.5.0 → **8.6.6** (Aspire.RabbitMQ.Client requires >= 8.6.6; NU1605
  downgrade-as-error otherwise). Aspire.Hosting.RabbitMQ pulls transitive MessagePack 2.5.192 with known
  NU1902/NU1903 vulns (warnings only, AppHost dev-orchestration only) — flag for hardening when the broker lands.
- RabbitMQ.Client is v7 (7.2.1): async API — `IConnection.CreateChannelAsync(CreateChannelOptions(pubConfirms:true,
  tracking:true), ct)`, `ExchangeDeclareAsync`, `BasicPublishAsync<BasicProperties>(exchange, routingKey, mandatory,
  props, ReadOnlyMemory<byte>, ct)`. `BasicProperties` settable Headers/MessageId/CorrelationId/Timestamp(AmqpTimestamp)/
  DeliveryMode(DeliveryModes.Persistent). Adapter is WIRED but UNVERIFIED against a live broker (per spec).
- Tests: `IntegrationOutboxTests` (10) drive the stores/tap directly (worker disabled via TestHostConfig
  `Messaging__DrainWorkerEnabled=false`). Includes a no-PHI architecture guard asserting booking + commission
  envelopes serialize ONLY whitelisted ID keys. Full suite 247 → **257**, green.
- INDEX (resolved): the inline `event_id UUID NOT NULL UNIQUE` is kept (that's what `ON CONFLICT (event_id)`
  targets); the explicit `idx_integration_outbox_event_dedup` was REMOVED from DDL + bundle + live DB by the
  team lead (it duplicated the column-constraint index `..._event_id_key`). So there's ONE unique index on event_id.
- TEST-ISOLATION (fixed by team lead): because the drain worker is off suite-wide, `integration_event_outbox`
  becomes a shared HOT table with a growing 'pending' backlog. ClaimDueAsync orders `next_retry_at NULLS FIRST`,
  so a fresh-seeded pending row (NULL next_retry_at) sorts among many others and a single `batchSize:50` claim can
  miss it → the claim/lease/skip-not-due and stranded-reclaim tests passed only on an empty table (1st run) and
  flaked on re-run. Fix: loop-drain in 500-batches until the seeded outbox_id is observed. See
  [[feedback_shared_hot_table_test_isolation]].
