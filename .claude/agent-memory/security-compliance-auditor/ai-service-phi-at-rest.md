---
name: ai-service-phi-at-rest
description: AI service (Python) PHI-at-rest envelope encryption + consent/break-glass gate — cleared PASS; crypto byte-for-byte interop with .NET verified; deferred bugs ruled non-leaks
metadata:
  type: project
---

Phase-3 slice `feat/phase3-ai-phi-at-rest` (PR after #25). Cleared **PASS** (no conditions).

**What it established.** The Python AI sibling (`ai_service/`) previously stored derived PHI in PLAINTEXT (encryption_key_id NULL) and its RAG/OCR endpoints had NO patient-consent gate. This slice closed both.

**Crypto (`ai_service/app/encryption.py`).** Byte-for-byte interoperable with .NET `FieldEncryptionService` + `LocalEnvelopeKeyManagementService` over the SAME `platform.encryption_keys` rows. Envelope = base64(utf8({keyId,wrappedKey,nonce,ciphertext,tag})), camelCase. wrappedKey layout = nonce(12)||tag(16)||ct(32) = 60 bytes (matches .NET `Concat` + the `[..12]/[12..28]/[28..]` slice). KEK = PBKDF2-SHA256(passphrase, salt, 100_000, 32); salt from `local-dev://{id}#{b64salt}`. Verified live with a cross-language re-impl: .NET-style unwrap recovers Python's data key exactly. Fails closed: no active `medical_history` key → `EncryptionKeyUnavailable` (never writes plaintext); destroyed key (`#DESTROYED` or status/destroyed_at) → `KeyDestroyed`. AI NEVER provisions keys (.NET/KMS only). `key_usage_log` written in the caller's tx (rolled-back insert rolls back its usage log).

**Consent gate (`ai_service/app/phi_access.py`).** `enforce_phi_gate` → `has_active_consent` (tenant-LESS query of docslot.patients, mirrors `Patient.HasActiveConsent = ConsentGivenAt!=null && DeletedAt==null && IsActive`; correct — patients is global cross-tenant identity) → on miss `active_break_glass_grant` (exact mirror of `BreakGlassService.GetActiveGrantAsync`: revoked_at IS NULL AND expires_at>NOW, NULL-resource matches patient-wide only, specific resource matches patient-wide OR exact, ORDER BY expires_at DESC LIMIT 1) → both miss = 403. `record_purpose_of_use` is FIRST-CLASS (not best-effort): break-glass stamps `is_break_glass=true, review_required=true, declared_purpose='emergency'` → lands in v_security_review_queue. Gate precedes EVERY PHI read/persist on all 3 endpoints (`/rag/index`, `/rag/ask`, `/extractions/lab-report`); no PHI touched before the 403.

**Tenant isolation.** AI service connects as DB OWNER (bypasses RLS) — isolation is code-enforced: every ai.* query filters `tenant_id = <JWT tenant>` AND patient scoping, plus `patient_tenant_links` gate. Tenant comes ONLY from the validated JWT claim (`auth.py`, HS256, iss/aud/exp/sub required) — NO client-header tenant override, no spoof vector. Commented `docslot_ai` least-priv role block in `10_roles_grants.sql` for when RLS becomes load-bearing (append-only respected: no UPDATE/DELETE on key_usage_log/purpose_of_use_log/audit_log).

**Bug #3 (read-that-writes) CLOSED.** `/rag/ask` no longer auto-indexes; only `/rag/index` writes embeddings. Only 2 PHI-table INSERT sites service-wide (insert_embedding, insert_extraction), both encrypt-before-bind, both gated. triage.py/predictions.py touch no clinical PHI tables.

**Accepted scope decisions (ruled NOT findings):**
- metadata keeps record_type/severity/icd10 PLAINTEXT; title encrypted as `title_enc` (popped before metadata store). Non-encrypted scalars per PR#25.
- chunk_text_hash = SHA256(plaintext) for dedup (acceptable — hash not reversible to PHI).
- extracted_data (analyte values + abnormalCount) plaintext JSONB; free-text raw_ocr_text IS encrypted (list view queries abnormalCount).
- OCR enforces consent gate but NOT `ai.documents.extract` RBAC perm (defined, unenforced) — pre-existing; RAG endpoints DO enforce `docslot.medical_history.read` via resolve_user_permissions fail-closed.
- Deferred bugs #10 (allows_phi model-gate non-enforcement) and #12 (cross-space embedding) are correctness/relevance, NOT consent-bypass or cross-tenant/PHI leaks. allows_phi lives on ai.ai_model_configs (model governance) — orthogonal to and downstream of the consent gate. Deferral acceptable.

**Registry:** 3 live rows in encrypted_fields_registry (ai.embeddings.chunk_text, ai.embeddings.embedding_vector, ai.ai_document_extractions.raw_ocr_text), all data_class='medical_history'/medical/consent — applied to live docslot_platform, verified. Column types: chunk_text TEXT, embedding_vector BYTEA (stores base64 ascii), raw_ocr_text TEXT.

**Secrets:** dev passphrase + JWT key are placeholder ("replace-in-production"), env-overridable (AI_ENCRYPTION_PASSPHRASE), .env gitignored. AI passphrase MUST equal .NET Encryption:Passphrase for shared-key interop + unified DPDP erasure.

**Tests:** 14/14 pass on `.venv` (NOT anaconda base — `ai_service/.venv/bin/python`). Real live-DB integration (conftest seeds fixed tenant/patients/key, cleans PHI leaf rows per test), adversarial: plaintext-absence asserts, 60-byte wrappedKey, fail-closed no-key + real key-destruction, zero-side-effects on 403 ×3, bug#3 (0 embeddings on ask), break-glass unlock+review flag, 422 patient-link.

**LOW/INFO residuals (non-blocking):** (1) purpose-of-use, consent check, PHI persist, and best-effort audit each open their OWN connection/tx — NOT atomic with each other; deliberate log-ahead design (over-log, never under-log) is the safe posture. (2) `_log_usage_for_payload` swallows decrypt-logging errors — acceptable (data already decrypted; logging must not raise), but means a decrypt could theoretically go unlogged in key_usage_log under a logging fault. (3) extract purpose-log lands before OCR runs, so a 422-on-OCR-failure still leaves a purpose row — safe-side.
