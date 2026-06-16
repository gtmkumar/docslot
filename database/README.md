# DocSlot Platform Database

Production-ready PostgreSQL schema for the DocSlot multi-product healthcare platform.

**Total: 113 tables across 8 schemas, designed for PostgreSQL 16+ with pgvector. Medical-grade security with DPDP Act 2023 compliance built in. AI-native via Python sibling service.**

---

## Architecture at a Glance

```
┌─────────────────────────────────────────────────────────────────┐
│  THIRD-PARTY APPS (Apollo HMS, Star Insurance, PharmEasy, ...)  │
│              ↓ OAuth 2.0 + scoped JWT tokens                     │
├─────────────────────────────────────────────────────────────────┤
│  platform_api.*  (8 tables)                                      │
│  api_clients · api_scopes · api_tokens · webhooks · events       │
├─────────────────────────────────────────────────────────────────┤
│  PRODUCT SCHEMAS                                                 │
│  ┌────────────────┐  ┌──────────────┐  ┌─────────┐  ┌────────┐ │
│  │ docslot.*      │  │ ruralreach.* │  │ safeher │  │ generic│ │
│  │ 26 tables      │  │ 8 tables     │  │ 8 tables│  │ 7 tabls│ │
│  │ Bookings,      │  │ Vans, routes │  │ Female  │  │ Drug   │ │
│  │ Doctors,       │  │ Cold chain,  │  │ staff,  │  │ equiv- │ │
│  │ Patients,      │  │ Sample chain │  │ chaper- │  │ alence │ │
│  │ Prescriptions, │  │ of custody   │  │ ones,   │  │ DB,    │ │
│  │ ABDM/ABHA,     │  │              │  │ privacy │  │ inter- │ │
│  │ WhatsApp       │  │              │  │ prefs   │  │ actions│ │
│  └────────────────┘  └──────────────┘  └─────────┘  └────────┘ │
├─────────────────────────────────────────────────────────────────┤
│  platform.*  (18 tables — SHARED BY ALL PRODUCTS)               │
│  tenants · users · permissions · roles · role_permissions ·     │
│  user_tenant_roles · audit_log · breach_log · billing · ...     │
└─────────────────────────────────────────────────────────────────┘
```

## Design Principles

1. **RBAC-first** — All access control via permissions → roles → users. No hardcoded role checks anywhere in application code.
2. **Multi-tenant isolation** — Every product row is scoped by `tenant_id`. Cross-tenant queries require explicit privilege.
3. **Platform-as-a-Service** — Third-party apps consume any product via scoped OAuth tokens. The `platform_api.*` schema is the gateway.
4. **Future-proof** — New products plug in as new schemas (`ruralreach.*`, `safeher.*`, `genericfirst.*`) without modifying the shared core.
5. **Audit everything** — Immutable `platform.audit_log` for DPDP/HIPAA compliance. Every sensitive read/write captured.
6. **UUID primary keys** — Globally unique, no sequence contention across distributed deploys.
7. **JSONB for extensibility** — Settings and metadata stored as JSONB so new fields don't require migrations.
8. **Soft deletes** — `deleted_at` timestamps; rows are never physically removed. Required for audit and DPDP grace periods.

## File Layout

| File | Purpose | Tables | When to run |
|------|---------|--------|-------------|
| `01_platform_core.sql` | Identity, RBAC, billing, audit | 18 | **Required** — always first |
| `02_platform_api.sql` | Platform-as-a-Service layer | 8 | **Required** — depends on core |
| `03_docslot.sql` | DocSlot product schema | 26 | **Required** for DocSlot |
| `04_future_products.sql` | RuralReach + SafeHer + GenericFirst | 22 | Optional — when those products launch |
| `05_security_hardening.sql` | Medical-grade security: encryption keys, audit chain, RLS, anomaly detection, DPDP rights | 13 | **Required for production with real patient data** |
| `06_ai_services.sql` | AI/ML schema: LangGraph workflows, embeddings, agent runs, predictions, OCR extractions | 10 | **Required if using DocSlot.AI Python service** |

## Execution Order

```bash
# Required for any deployment
psql -d docslot_platform -f 01_platform_core.sql
psql -d docslot_platform -f 02_platform_api.sql
psql -d docslot_platform -f 03_docslot.sql

# REQUIRED for production with real patient data
psql -d docslot_platform -f 05_security_hardening.sql

# Required if using DocSlot.AI Python service (LangGraph workflows, RAG, OCR, predictions)
psql -d docslot_platform -f 06_ai_services.sql

# Optional — only when adjacent products are being built
psql -d docslot_platform -f 04_future_products.sql
```

**Why 05 is required for production**: DocSlot handles confidential medical history. DPDP Act 2023 Section 8(5) mandates security safeguards proportionate to data sensitivity. Medical data is the highest sensitivity tier. Running 01-04 without 05 gives you a functional system but lacks: field-level encryption metadata, tamper-proof audit chain, row-level security policies, anomaly detection, break-glass access controls, and cryptographic deletion certificates required by DPDP Sections 11-12. Skip 05 only for dev/staging environments with synthetic data.

**Why 06 is for AI features**: DocSlot.AI is a sibling Python service that handles LangGraph workflows (symptom triage, prescription safety review), LangChain RAG (medical history Q&A), pandas/scikit-learn predictions (no-show forecasting), PaddleOCR pipelines (lab report extraction), and Whisper transcription (voice notes). The .NET service handles transactional booking and stays as the system of record. See `AI_ARCHITECTURE.md` for the full Python service design. Run `06_ai_services.sql` only if deploying DocSlot.AI.


## All-in-One Bundle: `docslot_complete.sql`

For convenience, all 6 SQL files are bundled into a single executable file: `docslot_complete.sql`. This is equivalent to running the 6 files in order, but in one command:

```bash
createdb docslot_platform
psql -d docslot_platform -f database/docslot_complete.sql
```

**The bundle is verified end-to-end** against PostgreSQL 16.14. A clean run produces:
- 113 tables across 8 schemas
- 317+ indexes
- 18 PostgreSQL functions
- All RLS policies, triggers, seed data, RBAC permissions

**Bundle properties**:
- **Order**: 01 → 02 → 03 → 05 → 06 → 07 → 08 → 09 → 04 (preserving all dependencies)
- **Progress markers**: prints `RAISE NOTICE` messages showing which part is loading
- **Verification footer**: counts tables at the end and reports SUCCESS/MISMATCH
- **Size**: ~225 KB (sum of all 6 source files plus markers)
- **NOT idempotent** — designed for fresh databases. To re-run: drop the schemas or the database first.

**When to use the bundle vs individual files**:
- **Use bundle** for: new dev environments, CI integration tests, staging setup, fresh production database
- **Use individual files** for: selective install (e.g., skip 05 for dev), incremental schema work, when you only need part of the platform

The source files in `database/*.sql` remain canonical. The bundle is regenerated from them — if you change a source file, also regenerate the bundle (a script does this automatically as part of the project's normal workflow).

## The RBAC Model

This is the heart of the system. **Permissions are bundled into roles. Roles are assigned to users. Users belong to tenants.**

```
                       ┌────────────────┐
                       │  permissions   │  granular: 'docslot.booking.approve'
                       │  (registered   │
                       │   per product) │
                       └───────┬────────┘
                               │
                               │ many-to-many
                               ▼
                       ┌────────────────┐
                       │     roles      │  bundles: 'tenant_admin', 'doctor'
                       │  (system OR    │
                       │   tenant-      │
                       │   custom)      │
                       └───────┬────────┘
                               │
                               │ assigned via
                               ▼
                       ┌────────────────┐
                       │ user_tenant_   │  user X has role Y in tenant Z
                       │     roles      │  (with optional expiry)
                       └───────┬────────┘
                               │
                               ▼
                       ┌────────────────┐
                       │     users      │  platform identity
                       │  (one user can │
                       │   belong to    │
                       │   N tenants)   │
                       └────────────────┘
```

### Why this design

- **No manual permission lists per user** — admins assign roles, not individual permissions
- **Job-specific bundles** — a "doctor" role contains all permissions a doctor needs; rename it, never re-assign
- **Tenant-scoped custom roles** — each hospital can create its own roles (e.g., "Pediatric Department Lead") without affecting other tenants
- **Time-bound grants** — `user_tenant_roles.expires_at` for consultants, support staff, locum doctors
- **Database-driven** — change permissions at runtime; no redeployment to add a permission to a role

### How application code checks permissions

```sql
-- Simple permission check (used by every API endpoint)
SELECT platform.user_has_permission(
    p_user_id := '...',
    p_permission_key := 'docslot.booking.approve',
    p_tenant_id := '...'
);
```

```csharp
// In .NET application
if (!await _authService.HasPermission(userId, "docslot.booking.approve", tenantId))
    return Forbid();
```

### Seeded System Roles

| Role Key | Scope | Description |
|----------|-------|-------------|
| `super_admin` | platform | Full access to entire platform (all tenants, all products) |
| `platform_support` | platform | Read-only access for customer support, can impersonate users |
| `platform_billing` | platform | Manage billing across all tenants |
| `tenant_owner` | tenant | Full access within own tenant |
| `tenant_admin` | tenant | Administrative access (no destructive operations) |
| `tenant_staff` | tenant | Day-to-day operational access (bookings, patient records) |
| `tenant_viewer` | tenant | Read-only access within tenant |
| `doctor` | tenant | Own data only (own schedule, own bookings, patient read) |

All system roles cannot be deleted. Custom roles (created per tenant) inherit the same permission registry.

## Platform-as-a-Service Layer

The `platform_api.*` schema lets third-party applications consume DocSlot (or any other product) as a service.

### Use case examples

- **Apollo Hospitals HMS** — wants to push their existing patient bookings into DocSlot. Gets API client + `docslot.bookings.write` scope.
- **Star Health Insurance** — wants to validate booking authenticity for cashless claims. Gets `docslot.bookings.read` + `docslot.patients.read` (with consent).
- **PharmEasy** — wants to receive prescription events to fulfill medications. Subscribes to `docslot.prescription.issued` webhook.
- **A new women's health app** — wants to use DocSlot's appointment engine but has its own frontend. Gets `docslot.slots.read` + `docslot.bookings.write`.

### How it works

1. Third-party registers as an `api_client` (manual approval by super_admin)
2. Client is granted scopes from `api_scopes` (e.g., `docslot.bookings.read`)
3. Client requests OAuth token via `client_credentials` grant
4. Token (JWT) is hashed and stored in `api_tokens` for revocation
5. Every API request logged to `api_requests` for rate limiting + analytics
6. Webhooks delivered via `webhook_subscriptions` with HMAC signing + retry logic

### Built-in scopes

- `docslot.bookings.read` / `.write` / `.approve` / `.cancel`
- `docslot.patients.read` / `.write` (requires consent)
- `docslot.doctors.read`
- `docslot.slots.read`
- `docslot.prescriptions.read` / `.write` (requires consent)
- `docslot.reports.read` / `.upload` / `.deliver` (requires consent)
- `docslot.abdm.records.fetch` / `.push` (requires consent)

## Future-Proofing — Adding a New Product

When you build RuralReach (or any future product), the process is:

1. **Create schema** — `CREATE SCHEMA newproduct;`
2. **Register product** — Insert into `platform.products`
3. **Register permissions** — Insert into `platform.permissions` with `product_id`
4. **Assign permissions to existing system roles** — `tenant_owner` gets all new product perms by default
5. **Register API scopes** — Insert into `platform_api.api_scopes` for third-party access
6. **Register event types** — Insert into `platform_api.api_event_types` for webhooks
7. **Build product tables** — All scoped by `tenant_id` referencing `platform.tenants`
8. **Add tenant subscription rows** — Insert into `platform.tenant_product_subscriptions` for each tenant using the new product

The shared core (`platform.*` and `platform_api.*`) does NOT need to change. This is enforced by convention — never add product-specific columns to platform tables.

## Multi-Tenant Isolation

Every product table (with rare exceptions like `docslot.patients` which is cross-tenant by design) includes `tenant_id` as a NOT NULL column with FK to `platform.tenants`.

**Application code MUST filter by tenant_id on every query.** Three layers of defense:

1. **Application layer** — Repository pattern with mandatory tenant scoping. Forgetting it is a code review catch.
2. **Database layer** — Composite indexes always start with `tenant_id` so queries without it become full scans (fail fast in dev/staging).
3. **Row-level security** (optional, defense-in-depth) — Can be enabled later with PostgreSQL RLS policies that use `current_setting('app.tenant_id')`.

## Patient Identity (Cross-Tenant by Design)

DocSlot's `patients` table is intentionally NOT tenant-scoped. Reason: a patient's phone number is their identity, and they visit multiple healthcare facilities.

- `docslot.patients` — one row per phone number (global)
- `docslot.patient_tenant_links` — maps which tenants each patient has visited, with tenant-local MRN
- `docslot.abdm_health_records` — FHIR R4 records with ABHA ID; sharing controlled by `docslot.abdm_consents`

This is what enables **Problem 1: Fragmented medical records** to be solved — a doctor at Hospital B can request access to records created at Hospital A with patient consent via ABDM.

## Audit and Compliance

The `platform.audit_log` table captures every action across the platform. **Never DELETE rows directly** — use retention jobs to archive old data instead.

Schema enforces DPDP Act requirements:

- `data_deletion_requests` — tracks right-to-erasure with 30-day grace period
- `breach_log` — 72-hour reporting requirement built into schema
- `consent_version` and `accepted_terms_version` on users + patients — track which version of T&C was accepted
- Soft deletes everywhere — actual deletion happens only after retention period

## Auto-Generated Identifiers

Bookings, prescriptions, and reports get human-readable IDs via triggers:

- `BKG-2026-04-00001` — Bookings
- `PRX-2026-04-00001` — Prescriptions
- `RPT-2026-04-00001` — Lab reports

These are safe to display in WhatsApp messages, PDFs, and patient communications. UUIDs remain the primary keys for internal use.

## Performance Notes

- **Partial indexes** used extensively — `WHERE deleted_at IS NULL`, `WHERE status = 'pending'` etc.
- **GIN trigram indexes** for fuzzy text search on names, drug names, test catalogs
- **Materialized view** `docslot.v_doctor_ratings` — refresh via cron, not on every query
- **`audit_log` partitioning recommended** — uncomment partitioning DDL when row count exceeds 10M

## Testing the RBAC

After running the SQL files, verify permissions work:

```sql
-- Create a test super admin
INSERT INTO platform.users (email, password_hash, full_name, is_platform_user, email_verified)
VALUES ('admin@example.com', crypt('test123', gen_salt('bf')), 'Test Admin', true, true)
RETURNING user_id;

-- Assign super_admin role (NULL tenant_id = platform-wide)
INSERT INTO platform.user_tenant_roles (user_id, tenant_id, role_id)
SELECT
    (SELECT user_id FROM platform.users WHERE email = 'admin@example.com'),
    NULL,
    (SELECT role_id FROM platform.roles WHERE role_key = 'super_admin');

-- Verify permissions
SELECT permission_key FROM platform.v_user_permissions
WHERE email = 'admin@example.com'
LIMIT 10;

-- Check specific permission
SELECT platform.user_has_permission(
    (SELECT user_id FROM platform.users WHERE email = 'admin@example.com'),
    'platform.tenants.suspend'
);
-- → true
```

## Migration Strategy

For an existing DocSlot deployment migrating to this unified schema:

1. **Phase 1 (week 1)** — Deploy 01 + 02 + 03 to a new database; data migration scripts move existing rows
2. **Phase 2 (week 2-3)** — Migrate application code to use new schemas; old database in read-only standby
3. **Phase 3 (week 4)** — Cutover; old database becomes archive

For greenfield deployments — just run all 4 files in order.

## What's NOT in this schema (intentional)

- **Application-specific config that changes per deployment** — store in `platform.platform_settings`
- **Cached data** — use a separate cache layer (Redis or PostgreSQL UNLOGGED tables)
- **File contents** — only metadata; actual files go to object storage (GCS / Azure Blob / S3)
- **Search indexes** — use Meilisearch / Typesense for full-text search; this DB has trigram for basic fuzzy matching only
- **Real-time analytics** — use a separate analytics DB; this is OLTP-optimized

---

## Security Architecture — Defense in Depth

DocSlot handles medical records, ABHA-linked health data, and PII covered by India's DPDP Act 2023. **Medical data is the most sensitive category** — DPDP penalties go up to ₹250 crore per breach, and the Digital Personal Data Protection Board can suspend operations. This is not a place for shortcuts.

The security design uses **7 layers**, any one of which can fail without compromising the data:

### Layer 1 — Application authentication (enforced before DB query)
- Password hashing with argon2id (configurable; bcrypt acceptable fallback)
- MFA mandatory for any role with medical data access (enforced in app code, flag in `platform.users.mfa_enabled`)
- Session tokens hashed in `platform.user_sessions` for revocation
- Failed login lockout after 5 attempts (`platform.login_attempts`)

### Layer 2 — Authorization (RBAC + column-level policies)
- Permission check on every API endpoint via `platform.user_has_permission()`
- Column-level access policies in `platform.access_policies` table — receptionist sees patient name but NOT medical history
- Purpose-of-use declaration required for medical record access (`platform.purpose_of_use_log`)
- Break-glass emergency access logged and auto-flagged for post-hoc review

### Layer 3 — Field-level encryption (data at rest)
- KMS-backed encryption for sensitive fields (`platform.encryption_keys`)
- Key material NEVER stored in DB — only KMS references (GCP KMS / Azure Key Vault / AWS KMS)
- Encrypted columns documented in `platform.encrypted_fields_registry`:
  - `docslot.patient_medical_history.description` and `.title`
  - `docslot.prescriptions.diagnosis`, `.medications`, `.examination`, `.chief_complaints`
  - `docslot.lab_reports.structured_results`
  - `docslot.abdm_health_records.fhir_bundle`
  - `docslot.patients.aadhaar_last_4`
  - `docslot.healthcare_facilities.whatsapp_access_token`
  - `platform.users.mfa_secret`
- Key rotation every 90 days (`encryption_keys.next_rotation_due_at`)
- Every encrypt/decrypt logged in `platform.key_usage_log` for forensics

### Layer 4 — Row-level security (database enforces tenant isolation)
- PostgreSQL RLS policies on the 5 most sensitive tables (medical history, prescriptions, reports, ABDM records, drug alerts)
- Application sets `SET LOCAL app.tenant_id = '...'` on every transaction
- Even if app code forgets `WHERE tenant_id = ?`, the DB blocks cross-tenant rows
- Super admin bypass via `app.is_super_admin = true` (impersonation logged)

### Layer 5 — Network controls
- IP allowlisting per tenant or per user (`platform.ip_allowlist`)
- Device fingerprinting (`platform.user_devices`) with trust levels: new → recognized → trusted → suspicious → blocked
- API rate limiting per client per endpoint (`platform_api.api_clients.rate_limit_*`)

### Layer 6 — Tamper-proof audit chain
- Every audit_log row chained to the previous via SHA-256 hash (`platform.audit_chain`)
- Any tampering with a historical row breaks the chain — detectable by `platform.verify_audit_chain()`
- Daily verification job; alerts on chain break (signals DB-level tampering)
- Chain head anchored daily to external transparency log (`platform.audit_anchors`) — provides cryptographic proof of audit log integrity even against compromised DBA

### Layer 7 — Anomaly detection & response
- `platform.anomaly_events` captures suspicious patterns:
  - Login from new country / new device
  - Impossible travel (login from 2 countries within physically impossible window)
  - Mass data access (reading 100+ patient records in short period)
  - Unusual data export
  - After-hours access patterns
  - Permission escalation attempts
  - Consent violations (accessing data without valid consent)
  - SQL injection patterns
  - Broken audit chain (signals tampering attempt)
- Auto-actions on critical anomalies: session revocation, MFA challenge, account lockout
- Review queue in `platform.v_security_review_queue` for security team

### DPDP Act 2023 — Compliance Mapping

| DPDP Section | Requirement | How DocSlot satisfies it |
|---|---|---|
| Section 5 | Data minimization | `aadhaar_last_4` only (not full Aadhaar), masking functions for unauthorized access |
| Section 6 | Consent management | `docslot.abdm_consents` + `platform.consent_event_log` (immutable history) |
| Section 8(4) | Purpose limitation | `platform.purpose_of_use_log` — staff must declare WHY accessing record |
| Section 8(5)(a) | Reasonable security safeguards | Encryption keys, audit chain, RLS, MFA — all layers above |
| Section 8(5)(b) | Encryption at rest and in transit | `encryption_keys` + TLS at app layer |
| Section 8(6) | Breach reporting within 72h | `platform.breach_log` with `reported_to_dpb_at` field |
| Section 8(7) | Accountability | Tamper-proof audit chain provides legally-admissible evidence |
| Section 11 | Right to portability | `platform.data_export_requests` — patient downloads FHIR R4 bundle |
| Section 12 | Right to erasure | `platform.deletion_certificates` — cryptographic key destruction |
| Section 13 | Right to correction | Standard CRUD via patient-facing app, audit logged |

### Cryptographic Erasure for Right-to-Erasure

When a patient invokes their right to erasure (DPDP Section 12), traditional approaches DELETE rows. But backups, replicas, and write-ahead logs still contain the data — physically destroying it is operationally impossible.

DocSlot uses **cryptographic erasure**: every patient's medical data is encrypted with a per-patient encryption key. To "erase" the data, we destroy the key in KMS. The ciphertext remains in the database (so foreign key constraints don't break), but it's mathematically impossible to recover plaintext without the key.

The `platform.deletion_certificates` table records:
1. Which keys were destroyed
2. SHA-256 hash of the database state before deletion
3. SHA-256 hash after deletion (proves change happened)
4. Digital signature from the platform certifying the erasure
5. PDF certificate given to the patient

This provides legally defensible proof of erasure that satisfies regulators and patients alike.

### Break-Glass Emergency Access

Real medical emergencies don't wait for permission checks. The break-glass pattern:

1. Doctor encounters unconscious patient in ER, needs medical history urgently
2. Doctor clicks "Emergency access" — must provide reason
3. System grants access immediately, logs everything in `platform.purpose_of_use_log` with `is_break_glass = true`
4. Patient is auto-notified within 24h via WhatsApp: "Dr. X accessed your records at Y hospital for emergency treatment"
5. Hospital admin reviews all break-glass events weekly (auto-flagged in `v_security_review_queue`)
6. Misuse triggers anomaly event + potential investigation

This balances clinical reality with patient privacy. Without break-glass, patients die because permission checks blocked access. With unaudited break-glass, doctors snoop on celebrity records. The audit-and-notify pattern is the industry standard.

### What's NOT in the database (handled by app/infrastructure)

- **TLS termination** — handled by reverse proxy / load balancer
- **WAF rules** — Cloudflare / cloud-native WAF
- **DDoS protection** — Cloudflare / cloud provider
- **Container security** — image scanning, runtime security in Kubernetes
- **Secrets management** — KMS (key material), HashiCorp Vault / cloud Secret Manager (DB passwords, API tokens)
- **Backup encryption** — encrypted backups via cloud provider features
- **Penetration testing** — quarterly external audit recommended

### Security Operations Checklist

Before going live with real patient data:

- [ ] All `platform.encrypted_fields_registry` entries have corresponding keys in `platform.encryption_keys`
- [ ] KMS provider configured and reachable from application
- [ ] Test encrypt/decrypt round-trip for every data class
- [ ] Audit chain verification scheduled (daily cron)
- [ ] External anchoring configured (transparency log, blockchain notary, or paper printouts in secure storage)
- [ ] Anomaly detection workers running (consume audit_log → write anomaly_events)
- [ ] IP allowlists configured for super_admin role
- [ ] MFA enforcement enabled for all roles with `docslot.patient.read` permission
- [ ] Break-glass procedure documented and trained
- [ ] Data export workflow tested with synthetic patient
- [ ] Deletion certificate workflow tested with synthetic patient
- [ ] Consent revocation webhook delivery tested for all subscribed API clients
- [ ] RLS policies verified — try cross-tenant queries with `app.tenant_id` set to another tenant; should return zero rows
- [ ] Breach notification template prepared (DPDP Section 8(6) requires 72-hour reporting)
- [ ] Security incident response runbook documented
- [ ] Quarterly penetration test scheduled
- [ ] DPDP Section 8(2) Data Protection Officer designated for the org


## Files 07 & 08 (added after initial schema)

### `07_commission_broker.sql` — Broker referral & commission (10 tables, `commission.*`)
Formalizes the Indian healthcare referral economy: registered brokers with PAN/Aadhaar KYC, 3 attribution mechanisms, tenant-configurable commission rules, TDS/GST-compliant payouts, disputes, campaigns. PCPNDT compliance enforced via CHECK constraints. Run after 01/03/05. Narrative doc: `../COMMISSION_SYSTEM.md`.

### `08_rbac_navigation.sql` — RBAC enhancements (5 tables, `platform.*`)
Backend-driven navigation menus (tenant_type-aware, bilingual), menu-permission gates, per-user permission overrides (deny-wins, time-boxable), resource/action lookup registries. Includes the performant `resolve_user_permissions()` set resolver and the rewritten `user_has_permission()` / `get_user_menus()`. Run after 01 (and 07 if commission menus should resolve their gating permissions). Narrative doc: `../RBAC_NAVIGATION.md`.

**Execution order with all files**: 01 → 02 → 03 → 05 → 06 → 07 → 08 → 09 → 04 (or just run `docslot_complete.sql`).
