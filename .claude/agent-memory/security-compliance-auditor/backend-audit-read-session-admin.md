---
name: backend-audit-read-session-admin
description: #86 audit-log READ tab + #87 active-session admin — actor-disclosure ruling, audit_log has NO RLS (explicit predicate is sole guard), session active_tenant_id scoping gap
metadata:
  type: project
---

Wave epic #80 Phase B added the first READ side of the write-only `platform.audit_log` (#86) and active-session oversight/revoke (#87). Cleared PASS-WITH-CHANGES (no blockers).

**Why load-bearing facts:**
- `platform.audit_log` has **NO RLS** (confirmed: no policy in 05_security_hardening; only tenant_id col + idx_audit_tenant). The `al.tenant_id = @tenant` predicate in `SecurityReadService.ReadAuditLogAsync` is the SOLE isolation guard. tenantId comes from `ICurrentUserContext.TenantId` (server JWT), never a client param; null tenant → empty page. Any future audit read MUST keep the explicit predicate.
- Audit read is well-minimized: selects audit_id/occurred_at/user_id/action/resource_type/resource_label/resource_id/ip/success/error_code/impersonator only. Deliberately EXCLUDES before_data/after_data/change_summary/error_message (all PHI-capable) and any chain internals. Keep that column list tight.
- **Actor-disclosure ruling (the #86 key question):** showing actor FULL NAME + EMAIL to a `tenant.audit.read` holder is ACCEPTABLE and NOT required to mask. Rationale: audit_log.user_id FKs platform.users (staff/platform only — patients live in docslot.patients and are NEVER platform users), so actor identity is staff PII not PHI; accountability ("who did what") is the audit tab's purpose and initials-masking defeats it; gated + strictly tenant-scoped. This is the sanctioned EXCEPTION to the initials-masking convention used on the review-queue / impersonation-oversight surfaces (SecurityReadService.ActorInitials) — those mask because they can surface cross-tenant support actors where initials suffice for triage.
- **resource_label is accepted in-scope PHI** for the audit read (e.g. "Patient: Goutam Roy") — searchable + in CSV. Per the #86 contract "no PHI leak beyond resource_label". If a future change widens the projection past resource_label, re-audit.
- **#87 session admin:** gate = `tenant.users.update` (accepted). Strictly member-scoped via EXISTS join on user_tenant_roles (revoked_at IS NULL + expires check). Single-revoke is one membership-guarded UPDATE (atomic, 0 rows→404); revoke-all is check-then-act throwing ForbiddenException (403) for non-members. NO token material projected (token_hash excluded). Writes run on request DbContext so the audit row commits inside the command UoW.
- **Open hardening (MEDIUM, non-blocking):** session list + revoke key only on session OWNER membership, NOT on `user_sessions.active_tenant_id`. For a user who is a member of ≥2 tenants, a Tenant-A admin can view metadata of / revoke that user's session established under Tenant B. Availability/metadata reach, not a non-member confidentiality leak. Recommendation: scope by active_tenant_id (or document global sign-out as intended). Revisit if multi-tenant staff memberships become common.
- Tests (SecurityAuditSessionTests + WebAppFactory) are GENUINE: Tenant B gets a real audit row (ForeignAuditMarker) + real foreign session; isolation asserts 0 rows / 404 / 403 and that the foreign session stays active. App under test runs as docslot_app (RLS-enforced).
