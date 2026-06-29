---
name: feedback_shared_hot_table_test_isolation
description: In the live-DB integration suite, a claim/drain test seeding ONE row into a shared worker-off outbox table must loop-drain until it observes its own row, not do a single fixed-batch claim.
metadata:
  type: feedback
---

When an integration test seeds ONE row into an outbox/queue table and then asserts a `ClaimDueAsync`-style
batch claim picks it up, do NOT rely on a single fixed-size claim (`batchSize:50`). Loop-drain in batches until
the test OBSERVES its own seeded id (e.g. claim 500 at a time, repeat until found, cap iterations).

**Why:** The mediq.IntegrationTests suite runs against the LIVE `docslot_platform` DB with the drain workers
DISABLED suite-wide (TestHostConfig: WhatsApp/webhook/`Messaging__DrainWorkerEnabled=false`). So every
outbox/queue table (`integration_event_outbox`, `webhook_deliveries`, `outbox_messages`) is a SHARED HOT table
with a growing backlog of un-drained 'pending' rows from other tests/prior runs — nobody is draining them. A
claim that orders `next_retry_at NULLS FIRST` puts every NULL-next_retry pending row ahead in line, so a single
`batchSize:50` claim can fail to include a freshly-seeded row. This is the classic empty-table-false-pass: the
first run passes on an empty table and the SECOND run flakes (this exact bug hit the phase-4
`ClaimDueAsync_…_Skips_NotDue` and `Stranded_…_Reclaimable` tests; the team lead caught + fixed it on re-run).

**How to apply:** Any new test in this suite that seeds N rows into a worker-off shared table and asserts a
claim/lease/skip transition must loop-claim until it sees its seeded id(s) before asserting — and prove
determinism by running the file TWICE (or `--filter` it twice), never trust a single green run on a table that
happens to be empty. Related: [[project_slice_phase4_integration_outbox]] and the user's
docslot-test-conventions auto-memory (live-DB suite, disabled workers, RLS roles).