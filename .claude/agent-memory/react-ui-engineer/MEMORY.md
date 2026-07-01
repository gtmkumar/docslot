# React UI Engineer — Memory Index

- [Frontend Contract Surface](project_contract-surface.md) — mock adapter signatures + menu/permission/badge/summary shapes the backend must mirror.
- [Foundation Patterns](project_foundation-patterns.md) — where the slide-over/permission/i18n/nav seams live and how to extend them in later waves.
- [Auth & RBAC Admin](project_auth-rbac.md) — Slice 01 frontend: session store, route guard, Team & Roles surface + which permission keys gate which actions.
- [Developer / API Platform Portal](project_developer-portal.md) — Slice 02 frontend: API clients/scopes/webhooks/logs, secret-shown-once UX, gating + flags.
- [Security & Compliance Console](project_security-console.md) — Slice 05 frontend: audit chain, DPDP export/erasure, breaches, break-glass, keys; irreversible-erasure UX + no-PHI rules.
- [Clinical Records UI](project_clinical-records.md) — Slice 03b frontend: prescriptions/reports/history/ABDM under patient detail; purpose-of-use gate + consent + break-glass + no-PHI-leakage rules.
- [Commission / Care Partners Console](project_commission-console.md) — Slice 07 frontend: Care Partners/attribution/rules/payouts/disputes; Care Partner terminology, PCPNDT-enforced, approve≠execute, PAN/PHI safety.
- [Live-API Seam](project_live-api-seam.md) — VITE_USE_REAL_API: seam layout, wired reads+writes (booking actions/add-patient w/ Idempotency-Key), enum/shape quirks, open gaps (no POST /doctors, wizard slots, manage-panel BOOKINGS.find).
- [Commission live wiring (PASS 3)](project_commission_live_wiring.md) — Care Partners + Calendar live quirks: omitted CommissionRuleDto keys, 204 writes, raiseDispute injects raisedBy, payout approve≠execute RBAC gate, calendar client-side slot rollup.
- [Platform-admin login](feedback_platform-admin-login.md) — verify Developers/Security live with admin@docslot.io/admin, NOT priyanka (tenant_owner gets 403 + no nav; mock fixture differs).
- [Support impersonation](project_impersonation.md) — issue #3: `impersonated_tenant` JWT claim, begin/end endpoints, session slice (impersonationId), `lib/jwt.ts` decoder, global banner.
- [IAM Roles & permissions matrix](project_iam-matrix.md) — Team & Roles IAM surface; assignment vs catalog plane gating split, seam fns, inert-permission caveat. Now the **6-tab unified console** (epic #80 Phase A): tab gating, People kebab/badges, roles master-detail, shared RoleMatrixView, KebabMenu primitive, empty-state stubs → #84/#85/#86/#89/#90/#91/#95.
- [Broker portal + Campaigns + Form 16A](project_broker-portal-campaigns-form16a.md) — Phase-2 commission: /portal self-service (IDOR-safe me/* + consent-OTP), Campaigns tab (budget bar), Form 16A TDS (full-PAN via transient blob, never in state).
- [AI document surfaces](project_ai-document-surfaces.md) — Slice 15: OCR extract + RAG ask (PHI mutations, purpose-of-use, out-of-cache) on patient view + /ai-ops non-PHI ops screen; nav gap flagged.
- [Team Audit log + Sessions](project_team-audit-sessions.md) — #86/#87: /security endpoints surfaced in /team; CSV auth-fetch, range-independent facets, useOptimistic revoke, lastActivityAt Online dot.
- [Invitations](project_invitations.md) — #89: token-based invites in /team Invites tab; one-time-token reveal panel, surgical-cache resend/revoke, list-has-no-inviter-name quirk, accept flow is out of scope (#93).
- [Branch/Dept SCOPE](project_branch-scope.md) — #90: People SCOPE column + All-branches filter + N-branches stat + manage-panel scope control; display-only (never confers perms), POST /branches unwired.
- [Security policy](project_security-policy.md) — #91: Security tab (2FA/password-session/access + IP allow-list) above #87 sessions; 3 perm planes, passthrough+Omit gotcha, N-of-M-2FA is client-derived, honesty labels, new ui/Toggle.
