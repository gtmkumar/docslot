---
name: backend-phase3-purpose-log-append-only
description: Re-audit PR#22 break-glass Finding 1 — purpose_of_use_log made append-only at substrate. VERDICT PASS. The load-bearing break-glass-slice carry-forward condition is now CLOSED.
metadata:
  type: project
---

Re-audit of my prior Finding 1 from the break-glass slice ([[backend-phase3-breakglass-unlock]]). Branch `feat/phase3-purpose-log-append-only`, audited 2026-06-29 (uncommitted working tree). VERDICT: **PASS** — Finding 1 is CLOSED. This was the highest-severity load-bearing carry-forward (the is_break_glass row is the sole record of a consent-override PHI read + populates v_security_review_queue; previously app held UPDATE and there was no substrate guard).

## What shipped (all verified live + source + bundle)
- `platform.block_purpose_log_update()` (05_security_hardening.sql, right after the purpose_of_use_log indexes ~line 372). Mirrors `block_audit_log_mutation` (01:578-592) EXACTLY: `IF current_user <> 'docslot_app' AND COALESCE(current_setting('app.allow_purpose_review',true),'off')='on' THEN RETURN NEW; END IF; RAISE EXCEPTION ... USING ERRCODE='insufficient_privilege';`. Control flow correct — the RAISE is the unconditional fall-through; no silent-pass path. RETURN NEW (post-image) is correct for an UPDATE-only trigger.
- Trigger `trg_purpose_log_no_update BEFORE UPDATE ON platform.purpose_of_use_log FOR EACH ROW`. Live: tgenabled='O' (enabled).
- `10_roles_grants.sql`: `REVOKE UPDATE ON platform.purpose_of_use_log FROM docslot_app;` + re-assert `GRANT SELECT, INSERT`. Live: has_table_privilege docslot_app UPDATE=f, DELETE=f, INSERT=t, SELECT=t.
- Bundle docslot_complete.sql: function body BYTE-IDENTICAL to source (diff clean); REVOKE/GRANT present (7267-7268). Source==bundle==live parity holds.

## Why this is a sound closure (defense-in-depth proven live)
Tested as the actual `docslot_app` role: it set `app.allow_purpose_review='on'` ITSELF then tried UPDATE → blocked with `permission denied for table` (grant layer fires before trigger). Escape hatch is confined by construction: even IF grant layer were bypassed, trigger's `current_user <> 'docslot_app'` guard blocks the app role regardless of GUC. The app can NEVER opt in. Two independent layers (REVOKE + trigger guard) each independently close the app-tamper vector I flagged.
- Owner UPDATE without opt-in → 42501 (trigger blocks even owner). Verified live.
- Owner UPDATE WITH `app.allow_purpose_review='on'` → succeeds (sanctioned review-close path; what the future review UI uses). Verified live.
- Only `gtmkumar` (owner/superuser) holds UPDATE; gated by trigger. No other UPDATE grantee. No unguarded vector.

## DELIBERATE: block UPDATE only, NOT DELETE — ACCEPTED
My original remediation said "BEFORE UPDATE/DELETE". The dev blocked UPDATE only. ACCEPTED because: (a) docslot_app holds NO DELETE grant on this table (verified live =f) ⇒ the APP-tamper vector (the actual threat I flagged) is fully closed; the residual owner-DELETE is the trusted-DBA threat, out of scope of Finding 1 which was explicitly about app-path tamper; (b) 5 integration-test factories hard-DELETE this table in teardown AS OWNER — a block-DELETE trigger would fire for owner too and break teardown + create tenant/user FK-delete ordering failures. Reasonable engineering tradeoff; the tamper vector that undermines break-glass accountability (silent UPDATE of is_break_glass/reviewed_at by the app) is closed at both layers.

## Residual (INFO, non-blocking, recorded for future GA hardening)
Owner/superuser can still hard-DELETE a purpose_of_use_log row (drops it from review queue) — same trust posture as audit_log's owner escape hatch, and the owner is the trusted DBA. If a future "tamper-evident even against owner" bar is set (e.g. external WORM/hash-chain export of the break-glass trail), block-DELETE + rewrite the 5 teardowns to truncate-by-tenant-cascade or use a transaction rollback fixture instead of explicit DELETE. NOT required now.

## Tests
New `SecurityHardeningTests.PurposeOfUseLog_Is_Append_Only_*` proves all 3 cases (app UPDATE→42501 grant, owner UPDATE no-optin→42501 trigger, owner+optin→succeeds). SecurityHardeningTests class 8/8 green; targeted test green. Build clean.

See [[backend-phase3-breakglass-unlock]] (the carry-forward this closes), [[backend-slice05-security-hardening]], [[backend-slice03b-clinical-phi]].
