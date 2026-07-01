# Security & Compliance Auditor — Memory Index

- [Outbox + WhatsApp send path](outbox-whatsapp-send-path.md) — how outbound WA messages flow; intent-agnostic drain; template/opt-in NOT enforced.
- [Cross-tenant SECURITY DEFINER sweep pattern](definer-sweep-pattern.md) — the established RLS-less maintenance-worker pattern and its hygiene checklist.
- [Brokers are platform-global identity](brokers-global-identity.md) — broker phone is UNIQUE platform-wide; tenant linkage via broker_tenant_links.
- [Consent + DPDP tables](consent-dpdp-tables.md) — where consent/purpose tables live; no marketing/promotional consent class yet.
- [Prior audit decisions](prior-audit-decisions.md) — conditions issued per wave so later waves can be checked against them.
- [Service-token PHI wall](service-token-phi-wall.md) — token_use=service worker identity; allow-by-default wall leaks via triage run-history; audit attribution broken (UUID FK).
- [Payout finalize side-effect scoping](payout-finalize-side-effect-scoping.md) — finalize re-queries ready_to_pay per (tenant,broker), not batch membership; cross-batch hazard.
- [Webhook delivery drain](webhook-delivery-drain.md) — Phase-4 durable async webhooks; webhook tables NOT RLS so no DEFINER; secret encrypted+registered; unconditional subscription-health bump defect.
- [Forwarded-headers trust model](forwarded-headers-trust-model.md) — edge XFF/per-IP limiter trust; .NET 10 KnownIPNetworks/KnownNetworks are one synced backing list; default-deny is spoof-proof.
- [Owner-rights view RLS bypass in IAM reads](owner-rights-view-rls-bypass-iam-reads.md) — views bypass RLS; effective-permissions/effective-access reads leak cross-tenant via ?tenantId; plain-table overrides read is safe.
- [Last-active-admin anti-bricking guard](last-admin-guard-antibricking.md) — permission-based guard in set_tenant_user_active + revoke_role_assignment; super_admin bypasses it; #79 test pins the blocking branch via a non-member global-override actor.
