---
name: slice05-security-hardening
description: DocSlot .NET slice 05 (security substrate) — canonical schema hardening, field encryption (envelope/KMS), DPDP rights (export/crypto-erasure/breach/consent), audit-chain verify+anchor, break-glass. First slice that edits canonical SQL.
metadata:
  type: project
---

Slice 05 (`05_security_hardening`) is the security substrate. Builds on [[slice01-platform-core]]/[[slice02-platform-api]]/[[slice03-docslot]]. **First slice that deliberately edits canonical `database/*.sql`** (auditor-requested).

**Why:** Lands the tracked carryover conditions (durable idempotency promoted to canonical, DB-level audit append-only, encryption registry) + the field-encryption prerequisite for clinical PHI (03b).
**How to apply:** Reuse the envelope-encryption + crypto-erasure + audit-verify patterns for any PHI/compliance work.

## Canonical schema edits (then REGENERATE bundle + RE-VALIDATE on fresh DB)
- `01_platform_core.sql`: added `platform.idempotency_keys` (durable idempotency, promoted from app-owned) + an **audit_log append-only guard** — `platform.block_audit_log_mutation()` trigger `trg_audit_log_append_only` BLOCKS UPDATE/DELETE (fires even for superusers, unlike REVOKE which superusers bypass; escape hatch: `SET app.allow_audit_maintenance='on'`).
- `03_docslot.sql`: added `docslot.slot_holds` (TTL holds, promoted from app-owned; tenant_id-leading indexes).
- `05_security_hardening.sql`: registered `platform_api.webhook_subscriptions.secret_hash` in `encrypted_fields_registry`.
- **Bundle**: regenerate via `database/regenerate_bundle.py` (concatenates the 9 numbered files in order 01,02,03,05,06,07,08,09,04 with PART banners). RE-VALIDATE on a throwaway DB: `createdb docslot_bundle_verify; psql -f docslot_complete.sql` → **115 tables (113+2) SUCCESS**; then `dropdb`. NEVER run the bundle against the live dev DB.
- Live dev DB aligned manually (dropped+recreated the 2 transient tables from canonical, added guard trigger + registry row). **App no longer issues DDL** — deleted `OperationalSchemaInitializer`.

## Field encryption (Layer 3)
`IKeyManagementService` (KMS abstraction) + `LocalEnvelopeKeyManagementService` (dev provider, kms_provider='local_dev'): envelope encryption, master KEK derived from `Encryption:Passphrase` config (PBKDF2 + per-key salt embedded in `key_reference`); **key material NEVER in DB** (encryption_keys stores only references). `IFieldEncryptionService`/`FieldEncryptionService`: registry-driven (reads `encrypted_fields_registry` → data_class → active key), per-record AES-256-GCM data key wrapped under the KEK, self-describing base64 envelope `{key_id,iv,ct,tag}`, logs every encrypt/decrypt to `key_usage_log`. Decrypt resolves key by envelope's key_id (works across rotation) and FAILS CLOSED if the key was destroyed. Registered fields incl. `platform.users.mfa_secret`.

## Crypto erasure (DPDP §12) — KEY DESTRUCTION not row deletion
`CryptoErasureService`: destroys the subject's data-class keys via `kms.DestroyKeyAsync` (marks status='destroyed', destroyed_at, AND scrubs the wrapping salt in key_reference → KEK unrecoverable → all wrapped data keys permanently unrecoverable). Records `deletion_certificates` (destroyed_key_ids, pre/post state hash, signature). Ciphertext rows stay (FKs intact). Requires a `data_deletion_requests` row (FK). Proven: decrypt works before erasure, throws after.

## Other DPDP + audit
- Export (§11): `DataExportService` assembles a FHIR-R4-shaped Bundle (Patient + Appointment entries; clinical resources with 03b). `data_export_requests` row.
- Breach (§8(6)): `BreachReportingService` → `breach_log` with 72h `reported_to_dpb_at`.
- Consent (§6): `ConsentEventLogger` → append-only `consent_event_log`.
- Audit verify/anchor (§8(7)): `AuditChainService.VerifyAsync` calls `platform.verify_audit_chain()`; `AnchorAsync` records the head to `audit_anchors`.
- Break-glass (Layer 2): `BreakGlassService` → `purpose_of_use_log` with `is_break_glass=true` + `review_required=true` (surfaces in `v_security_review_queue`); mandatory justification.

## Endpoints (api/v1/security/*, gated by slice-05 seeded keys)
`GET audit-chain/verify` (`platform.audit.verify_chain`), `POST audit-chain/anchor` (`platform.audit.anchor`), `POST dpdp/export` (`platform.export_requests.process`), `POST dpdp/erase` (`platform.deletion.certify`), `POST breaches` (`platform.breach.read`), `POST break-glass` (`docslot.medical_access.break_glass`).

## GOTCHA — audit chain is NOT concurrency-safe (flag for auditor)
`trg_audit_chain` reads the current chain head at INSERT time; concurrent audit writes race → duplicate previous_hash → broken chain. The integration test assembly MUST disable xUnit parallelization (`[assembly: CollectionBehavior(DisableTestParallelization=true)]` in AssemblyInfo.cs) since all tests share the live DB. To repair a broken chain: disable trg_audit_chain, TRUNCATE audit_chain RESTART IDENTITY, re-append all audit_log rows in occurred_at order, re-enable. Production needs serialization or a different chaining strategy at scale.
The append-only test runs in a ROLLED-BACK transaction so the probe leaves no chain residue (deleting a chained audit row would itself break the chain — the very thing being protected).

## Deferred
- Anomaly detection worker (`anomaly_events`) — stub/not built this slice (needs an audit_log→anomaly consumer). FLAGGED.
- Clinical PHI tables (prescriptions/labs/abdm/med_history/drug_alerts) → 03b: the field-encryption service is the prerequisite now in place; the read/write paths + RLS `app.is_super_admin`/`app.tenant_id` wiring land with 03b.
- access_policies column-level enforcement, ip_allowlist, user_devices, audit anchoring to a real external store — registered/available but not wired into request path this slice.

## Verify
`dotnet build` 0 errors (2 transitive MessagePack warnings). `dotnet test` 25/25 (19 prior + 6 slice-05). Bundle validated on fresh DB (115 tables). Live chain intact (0 broken) after serialized run.
