---
name: prior-audit-decisions
description: Conditions and verdicts issued per wave, so later waves can be checked against unmet conditions.
metadata:
  type: project
---

Running log of verdicts/conditions I have issued. Check new waves against any OPEN conditions.

## Phase-2 — Hidden-Care-Partner conversion nudge (run_partner_nudge_sweep) — VERDICT: PASS WITH CONDITIONS
Scope: `database/09_chat_identity.sql` (new SECURITY DEFINER fn), `BookingMaintenanceWorker.cs`, `WhatsAppAbstractions.cs` (IPartnerNudgeStore), `WhatsAppRepositories.cs` (PartnerNudgeStore), `DependencyInjection.cs`, new `HiddenPartnerNudgeTests.cs`.
Tenant isolation, DEFINER hygiene, PHI (generic text, no patient identity), anti-spam (cooldown + ≥3 threshold), broker exclusion: all PASS.
OPEN CONDITIONS before any PRODUCTION promotional send (the real Meta path) is enabled:
1. (HIGH) Proactive marketing must go via a Meta PRE-APPROVED TEMPLATE, not free-form text. MetaWhatsAppSender currently sends type:"text" only with no per-intent branching. Until a template-send path keyed by message_intent='partner_nudge' exists, the nudge MUST NOT ship to the real Meta sender. See [[outbox-whatsapp-send-path]].
2. (HIGH) DPDP lawful basis: record/track promotional-outbound opt-in (or documented legitimate-use basis) per recipient, and honor a STOP/opt-out keyword that writes consent_revoked and permanently suppresses nudges. No promotional consent class exists today. See [[consent-dpdp-tables]].
3. (MEDIUM) Outbox scrub: mark_outbox_sent/mark_outbox_failed redact only consent_otp/claim_otp bodies. partner_nudge body is non-secret marketing text, so no redaction needed — but confirm no PHI ever enters the text (today it's clinic display_name only — OK).
These are PASS-WITH-CONDITIONS items: the DEV/stub path (logs only) is safe to merge now; the conditions gate enabling the live Meta promotional send.
