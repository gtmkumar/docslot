---
name: audit-log-append-only-teardown
description: platform.audit_log blocks UPDATE/DELETE for EVERYONE (owner included) via a BEFORE trigger — seeded audit rows can't be torn down; use a unique marker instead.
metadata:
  type: feedback
---

When an integration test seeds rows into `platform.audit_log`, DO NOT try to `DELETE` them in teardown — even on the RLS-exempt owner connection. `trg_audit_log_append_only` (database/01_platform_core.sql) is a `BEFORE UPDATE OR DELETE` trigger that `RAISE EXCEPTION` unconditionally (not role-gated), so a teardown DELETE throws and fails the fixture dispose.

**Why:** the audit trail is legally append-only (DPDP/HIPAA tamper-evidence) and is additionally hash-chained (`trg_audit_chain`). The append-only guard is by design and applies to `audit_chain` too. INSERT is fine (that's how tests and `AuditTrailWriter` add rows).

**How to apply:** stamp a unique marker into `resource_label` (e.g. `AUDIT-{guid:N}`) at seed time and assert/filter on it via the read API's `search` param, then just leave the rows behind — the table already grows unbounded across the suite (64k+ rows) and that's expected. Same posture for `purpose_of_use_log` and `key_usage_log` (INSERT-only for docslot_app). Reads: `platform.audit_log` has NO RLS, so app-side tenant scoping is an explicit `tenant_id = @tenant` predicate (see the #86 Audit-tab read in SecurityReadService), not RLS. Related: [[shared-hot-table-test-isolation]].
