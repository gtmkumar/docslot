---
name: frontend-settings-screen
description: Workspace Settings surface (Phase 1) — WhatsApp token never on wire, PATCH whitelisted to 2 sections, can()-gated with distinct forbidden card, language switcher is locale-only
metadata:
  type: project
---

Workspace Settings screen (`frontend/src/features/settings/`, audited PASS 2026-07-03) over the existing GET/PATCH `/api/v1/settings` (SettingsController, gated tenant.settings.read/.update; facility row bound from JWT tenant, never a client param).

Security invariants verified (re-check if this surface expands):
- **No WhatsApp secret on the wire.** `WhatsappSettingsSchema` (contracts.ts) is a STRICT zod object {connected, phoneNumberId, verifiedAt} — omits the access token; even if the server leaked it, zod strips it (only the top-level Settings uses `.passthrough()`, and the token would live under `whatsApp`). WhatsappSection.tsx is read-only, renders phoneNumberId + a FIXED platform webhook path (`/api/v1/whatsapp/webhook`, not a secret). Backend SettingsController comment confirms whatsapp_access_token never returned.
- **PATCH not widened.** `UpdateSettingsRequestSchema` + `real.updateSettings` both hard-whitelist only `businessHours` and `appointmentSettings`; the body is constructed field-by-field, so extra keys can't reach the endpoint. No Idempotency-Key (config write, not money/booking).
- **Gating is can()-only, no role branch.** SettingsScreen uses `can('tenant.settings.read'/'.update')`; the query only runs when canRead; `!canRead` → distinct forbidden Lock card (never clean-slate empty); 404 → distinct "not set up" card; rail HIDES the 3 gated entries without read. Nav gate: `settings.tenant` menu → tenant.settings.read (08_rbac_navigation.sql; doctor excluded, verified 0 grants live).
- **Language switcher = device-only.** `setLanguage` (i18n.ts) persists ONLY 'en'|'hi' to localStorage key `docslot.lang`. No PHI/secret.
- **Mock seed (lib/mock/settings.ts) carries NO PHI/secret** — facility config + a phoneNumberId (public identifier) only; token never modelled.

Wire quirk: live API serializes the field as `whatsApp` (capital A, .NET camelCasing of C# `WhatsApp`), not `whatsapp` — contract + mock both match the running endpoint. Related: [[frontend-rbac-gating]].
