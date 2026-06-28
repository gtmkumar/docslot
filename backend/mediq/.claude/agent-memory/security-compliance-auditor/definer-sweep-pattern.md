---
name: definer-sweep-pattern
description: The established cross-tenant RLS-less maintenance-worker pattern (SECURITY DEFINER sweeps) and the hygiene checklist every new sweep must meet.
metadata:
  type: project
---

DocSlot's maintenance workers (BookingMaintenanceWorker, OutboxDrainWorker) run with NO `app.tenant_id` set, so they cannot satisfy RLS. They reach RLS-protected tables via SECURITY DEFINER functions that legitimately bypass RLS and self-enforce tenant correctness via JOIN/WHERE data.

Established sibling sweeps (all in `database/03_docslot.sql`, all SECURITY DEFINER, all `SET search_path = docslot, pg_temp`, all granted to docslot_app):
- `expire_stale_slot_holds()` (~1094)
- `expire_stale_consent_otps()` (~1151)
- `requeue_stranded_outbox()` (~1122)
- `claim_due_outbox` / `mark_outbox_sent` / `mark_outbox_failed` (~1235+)
- `commission.expire_stale_attribution_claims()` (`07_commission_broker.sql:901`)
- `docslot.run_partner_nudge_sweep(INT, INTERVAL)` (`09_chat_identity.sql:223`) — the Phase-2 hidden-Care-Partner nudge.

**DEFINER hygiene checklist (what to verify on each new sweep):**
1. `SECURITY DEFINER` + `SET search_path = <schema>, pg_temp` PINNED (blocks search_path hijack). ✓ pattern.
2. `GRANT EXECUTE ... TO docslot_app` only (least privilege). ✓ pattern.
3. NO dynamic SQL (no EXECUTE/format of identifiers). ✓ pattern.
4. NO mutation/DELETE of `platform.audit_log` (append-only hash chain). ✓ pattern — sweeps never touch audit_log.
5. Every write tenant-scoped by DATA, not RLS: outbox INSERT carries the row's own tenant_id pulled from the source table; cross-tenant COUNT/UPDATE correlated by `x.tenant_id = y.tenant_id`. Verified for run_partner_nudge_sweep (the 90d behalf-booking COUNT is scoped `bk.tenant_id = wcp.tenant_id`; the outbox INSERT carries `e.tenant_id`; the mark-back joins on `(tenant_id, phone)`). No cross-tenant leak.

The owning .NET store is a trivial passthrough (e.g. `PartnerNudgeStore` in `WhatsAppRepositories.cs`) calling the fn via parameterized `SqlQueryRaw`. DI in `mediq.Infrastructure/DependencyInjection.cs`.
