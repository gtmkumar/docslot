---
name: phase-d-invite-notifier-geoip-seams
description: #93 IInvitationNotifier + #94 IGeoIpResolver offline seams — token hygiene, advisory dispatch, tenant-scoped city enrichment
metadata:
  type: project
---

Phase-D (epic #80) added two offline provider seams, mirroring the AddWhatsAppSender honest-stub pattern.

**#93 IInvitationNotifier** (mediq.Application/Abstractions/InvitationAbstractions.cs). Default `StubInvitationNotifier` (Infrastructure/Security) records the intended send, NO delivery. Wired in `CreateInvitationCommandHandler` + `ResendInvitationCommandHandler` via `InvitationNotify.AdvisoryAsync` — dispatched AFTER the SECURITY DEFINER write + audit row, try/catch swallows all non-cancellation throws (advisory/non-blocking, cannot alter authz). Token hygiene: audit records email only (never token/hash); stub logs masked email at info, and at DEBUG logs only `Token.Length` — never the token string. `IInvitationTokenFactory` persists only SHA-256 hash; plaintext returned once. `AddInvitationNotifier` currently ALWAYS falls back to stub (no live email/WA transport exists yet) — provider config is read but unused.

**#94 IGeoIpResolver** (mediq.Application/Abstractions/GeoIpAbstractions.cs). Default `NullGeoIpResolver` returns null for every IP, no external call. Wired into `SecurityReadService.ReadAuditLogAsync` (page rows only; export path stays city-less) + `SessionAdminService.ListActiveForTenantAsync`. One lookup per DISTINCT IP. City is display-only, never a tenant guard. Enrichment runs on an already-stored IP; the underlying reads are tenant-scoped (audit_log predicated on `al.tenant_id=@tenant` from signed ctx — audit_log has NO RLS so that predicate is the sole guard; sessions predicated on `active_tenant_id` + active membership). DTOs `AuditLogRowDto.City` / `ActiveSessionDto.City` appended as nullable-with-default (back-compat). DI registers `NullGeoIpResolver` unconditionally (singleton) — no live geo provider wired yet.

**Auditor verdict (2026-07-01):** PASS, no required changes. Both defaults make zero network calls; no token leak; consent basis for invite send is sound (invitee named by admin holding tenant.users.create; send is the accept mechanism). INFO for the future LIVE geo step only: a hosted resolver (ip-api.com/ipinfo) would transfer end-user IPs to a third party — prefer local MaxMind GeoLite2 DB, or add a DPDP transfer basis before enabling a hosted provider. See [[prior-audit-decisions]].
