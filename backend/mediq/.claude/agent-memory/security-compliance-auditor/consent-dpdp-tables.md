---
name: consent-dpdp-tables
description: Where DPDP consent/purpose tables live, and the gap — no marketing/promotional-outbound consent class exists yet.
metadata:
  type: project
---

DPDP consent & purpose infrastructure (in `database/05_security_hardening.sql`):
- `platform.consent_event_log` (~551) — immutable proof-of-consent log. event_type ∈ {consent_requested, consent_granted, consent_modified, consent_revoked, consent_expired, consent_used, consent_denied}. Has `legal_basis`, `consent_scope` jsonb, `channel` ∈ {whatsapp, app, web, ...}. Keyed by `patient_phone` + tenant_id.
- `platform.purpose_of_use_log` (~341) — DPDP s8(4) purpose limitation; break-glass + review-required flags.
- `platform.encrypted_fields_registry` (~119, seeded ~133) — declares every encrypted column + data_class + legal_basis.
- `docslot.wa_contact_profiles.history_sync_consent` (bool) — the ONLY contact-level consent flag today, and it's specifically for WhatsApp-history→app-timeline sync, NOT for promotional outbound.

**Gap (Phase-2 nudge wave):** there is NO consent class / opt-in flag for PROACTIVE PROMOTIONAL outbound (the "become a Care Partner" marketing nudge). The nudge relies on "prior booking relationship" as its basis. The consent_event_log channel enum supports 'whatsapp' and could record a promotional-consent grant/revoke, but nothing wires it. Before production promotional send, expect: (a) a recorded lawful basis / opt-in per recipient, and (b) a STOP/opt-out keyword handler that writes consent_revoked and suppresses future nudges.
