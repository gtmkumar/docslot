---
name: audit-log-fk-cleanup
description: Test cleanup trap — never hard-DELETE platform.users/tenants that have audit_log rows; soft-delete + archive instead
metadata:
  type: feedback
---

In DocSlot integration tests, NEVER hard-`DELETE FROM platform.users` (or `platform.tenants`) for any
user/tenant that performed an audited action during the test.

**Why:** `platform.audit_log` has FKs `audit_log_user_id_fkey` (→ users.user_id),
`audit_log_impersonator_user_id_fkey`, and `audit_log_tenant_id_fkey` (→ tenants.tenant_id). Any action that
writes audit (approve/cancel/complete/reschedule/check-in all do) creates an audit_log row, and CLAUDE.md says
audit_log must never be DELETEd. A hard DELETE of the user then fails with `23503` FK violation. This bit the
first run of `BehalfConsentOtpTests` — the two tests that created an on-demand admin and called approve failed
in `CleanupNumbersAsync`, not in their assertions.

**How to apply:** Mirror `DocslotWebAppFactory.DisposeAsync`: soft-delete the user
(`UPDATE platform.users SET deleted_at=NOW(), is_active=false, email='deleted+'||user_id||'@...'`) and archive
the tenant (`UPDATE ... SET deleted_at=NOW(), status='archived'`). You MAY delete `user_sessions`,
`login_attempts`, and `user_tenant_roles` (no audit FK). Booking deletes are fine — audit_log has no booking FK.
