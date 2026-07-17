---
name: tenant-suspend-permission-split
description: Tenant lifecycle suspend/reactivate uses dangerous platform.tenants.suspend + suspended_reason (split from platform.tenants.update). VETO issued then CLEARED 2026-07-16.
metadata:
  type: project
---

RESOLVED 2026-07-16: VETO issued, split implemented, re-audited PASS. Final shape in tree: edit path `PUT /tenants/{id}` (platform.tenants.update) carries NO status — `UpdateTenantRequest` has no Status field, `TenantRepository.UpdateAsync` SET list has no status. Status is writable ONLY via `SetStatusAsync`, reachable ONLY via `PUT /tenants/{id}/suspend` + `/reactivate` (AdminController), both `[RequirePermission("platform.tenants.suspend")]` (dangerous). Handler `SetTenantStatusCommandHandler` (TenantStatus.cs): reason `.NotEmpty().When(!IsActive)` → 422 on suspend without reason; writes `suspended_reason` on suspend, clears (NULL) on reactivate; audit action `'suspend'`/`'reactivate'` (distinct verbs), ResourceId=tenantId, actor TenantId NULL. Entity `Tenant.SuspendedReason` maps to `suspended_reason` (PlatformConfigurations.cs:79). Frontend ManageTenantPanel: `canSuspend=can('platform.tenants.suspend')` distinct from `canEdit=can('platform.tenants.update')`. super_admin holds the perm via the "ALL permissions" grant (01_platform_core.sql:302-307). Non-blocking residual: no negative-auth integration test (update-perm-only principal → 403 on /suspend) — the RequirePermission gate enforces it, but a regression test would harden it. This is the mirror of the broker pattern (CommissionController SuspendBroker/ActivateBroker, commission.broker.suspend).

--- ORIGINAL VETO (historical) ---

VETO finding on the 2026-07-16 "edit tenant" wave (feat: tenant edit PUT /api/v1/tenants/{id}).

The edit form let a holder of the NON-dangerous `platform.tenants.update` set status to 'suspended'/'active' with NO reason captured — bypassing the schema-defined DANGEROUS permission `platform.tenants.suspend` (database/01_platform_core.sql:217, is_dangerous=true) and never writing the `suspended_reason` column (platform.tenants).

**Why:** platform.tenants carries NO RLS — the permission gate IS the only security boundary. The schema authors deliberately split suspend out as dangerous + reason-carrying because suspending a clinic cuts off patient-care access (high blast radius). Riding the generic update perm is a privilege downgrade / control bypass.

**How to apply:** Any tenant status transition to/from 'suspended' MUST gate on `platform.tenants.suspend` (dangerous), require a mandatory reason written to `suspended_reason` (and cleared on reactivate), and emit a distinct audit action ('suspend'/'reactivate') rather than action='update' with a status note. The plain contact-field edit path (`platform.tenants.update`) must NOT accept Status at all. Separation-of-duties: distinct edit vs suspend permission.

Verified GOOD on the same wave: tenant_code/tenant_type immutable (absent from DTO + UPDATE SET list); nav 'tenants' node gated on platform.tenants.read via menu_permissions so clinic staff never see it (applies_to_tenant_types=NULL is a tenant-TYPE filter, not a perm widener); audit records target tenant via ResourceId (TenantId=null actor-scope is correct for a platform act); PUT returns lean TenantDto (no PHI/secret) so no IDoNotCacheResponse needed. See [[settings-screen-phase1]].
