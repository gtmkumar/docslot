# Prescription Consultation — Implementation Plan

> Status: **Approved for build, not yet started.** Feature design for the doctor
> prescription composer + nurse/assistant intake split + doctor-scoped views.
> Owner decisions captured inline (see "Decisions locked").

## Decisions locked (from product review)

1. **No new nurse role.** Access is gated by **two permissions**, not a role check:
   - `docslot.prescription.draft` — "has access to this feature" → can fill the
     **intake part** (vitals + chief complaint + quick-start template pick).
   - `docslot.prescription.create` — the doctor-level permission → the **rest**
     (diagnosis, medications, investigations, advice, follow-up) **and Finalize/sign**.
   - Non-holders of `create` see the clinical sections **disabled/read-only** and
     **no Finalize button**. "For doctor role" = expressed as the permission the
     doctor role holds — never a `role === 'doctor'` check in JSX (design-DNA hard rule).
2. **Vitals = standard clinical PHI** — purpose-of-use gated on read, stored
   **unencrypted** like `diagnosis`/`advice` (NOT in `encrypted_fields_registry`).
3. **Doctor dashboard = the existing Overview screen, doctor-scoped** (no new route).
4. **Patient list "only confirmed" applies to doctors only** — reception keeps the
   full list.
5. **"Confirmed" = booking status `confirmed` or `checked_in`, slot date today or
   future** (any active/upcoming confirmed booking). Past/cancelled/no-show excluded.

---

## 0. Goals & principles

- **One consultation record, two roles, one signing transition** (`draft → finalized`).
  No duplicate documents.
- **Doctor author is server-derived** from the authenticated user
  (`docslot.doctors.user_id`); the client never asserts who signed.
- **Finalize = the doctor's legal act** (MCI: only a registered doctor prescribes),
  gated by `prescription.create`.
- Respect existing invariants: `tenant_id` + RLS on every row, soft-delete,
  hash-chained audit, Idempotency-Key on writes, `PRX-YYYY-MM-NNNNN` human IDs,
  bilingual en/hi, design-tokens-only, WhatsApp-first delivery.

---

## 1. Data model — `database/03_docslot.sql` (+ regenerate `docslot_complete.sql`)

**`docslot.prescriptions` — add columns (additive, backward-compatible):**

| Column | Type | Why |
|---|---|---|
| `vitals` | `JSONB DEFAULT '{}'` | BP/pulse/temp/spo2/weight. Nurse/assistant-writable. Standard PHI. |
| `drafted_by_user_id` | `UUID REFERENCES platform.users(user_id)` | who prepped the draft (audit separation from author) |
| `finalized_by_user_id` | `UUID REFERENCES platform.users(user_id)` | who signed (the doctor) |
| `finalized_at` | `TIMESTAMPTZ` | signing timestamp |

- Reuse existing columns: `examination`, `investigations` (JSONB), `advice`,
  `follow_up_in_days`, `status ('draft','finalized','delivered','amended')`,
  `medications` (JSONB), amendment lineage (`supersedes_prescription_id`).
- **`medications` JSONB shape upgrade** (no DDL — it's JSONB): structured per item —
  `{ name, strength, form, dose: {morning,noon,night} | "SOS" | "weekly",
  timing: "after_food"|"before_food", durationDays, instructions }`.
  This is what powers the auto WhatsApp reminder schedule.
- **Integrity trigger/CHECK:** `status='finalized'` ⇒
  `finalized_by_user_id IS NOT NULL AND finalized_at IS NOT NULL`.
  Enforces "signed rows have a signer" in the schema, not just code.
- **PHI:** vitals are purpose-of-use gated on read (same gate as other clinical
  fields). NOT added to `encrypted_fields_registry`.
- **Migration:** bundle is not idempotent; changes are additive. For the live dev DB,
  apply `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` deltas.

---

## 2. RBAC — `database/03_docslot.sql` (permissions + role grants)

- **New permission:** `docslot.prescription.draft` — "Prepare a consultation draft /
  record vitals (PHI); does NOT issue." `is_phi = true`.
- **Unchanged gates:** finalize → `docslot.prescription.create`;
  amend → `docslot.prescription.amend`; read → `docslot.prescription.read`.
- **Grants (no new role — attach to whatever roles a tenant already uses):**
  - Any staff role granted feature access → `prescription.draft` + `prescription.read`
    (+ `patient.read`). **Not** `.create`.
  - Doctor role → `.draft` + `.create` + `.amend` + `.read`.
- **Auditor gate:** new PHI permission + author/signer separation →
  `security-compliance-auditor` sign-off required before Phase B merges.

---

## 3. Backend API (.NET — `backend/mediq`, database-first when scaffolded)

Contract-first definitions the frontend seam calls:

- `POST /api/v1/consultations` → create/get the **draft** for a `bookingId`
  (idempotent per booking). Gated `prescription.draft`. **Author not set here.**
- `PATCH /api/v1/consultations/{id}` → save draft fields (vitals, complaints, dx,
  meds, investigations, advice, follow-up). Gated `prescription.draft`.
- `POST /api/v1/consultations/{id}/finalize` → `draft → finalized`; sets
  `finalized_by_user_id`, `doctor_id` **derived server-side** from
  `docslot.doctors WHERE user_id = sub` (reject if caller isn't a doctor); mints PRX
  number; runs `drug_alerts`; generates PDF; triggers WhatsApp send. Gated
  `prescription.create`. Carries Idempotency-Key.
- `GET/POST /api/v1/doctors/me/templates` → per-doctor quick-start templates +
  favourite drugs (new table `docslot.rx_templates`, Phase C).
- Any endpoint returning decrypted PHI → `IDoNotCacheResponse` (idempotency store is
  plaintext).

---

## 4. Frontend seam & contracts

Files: `src/lib/mock/contracts.ts`, `src/lib/backend/{real,mock,index}.ts`,
`src/lib/mock/clinical.ts`.

- **Extend `MedicationSchema`** → structured dosing object (§1). Add a display-string
  helper for the preview + reminder text.
- **New contracts:** `ConsultationDraft`, `Vitals`, `SaveConsultationRequest`,
  `FinalizeConsultationRequest` (**no `doctorId`** — server-derived).
- **Deprecate** the current `IssuePrescriptionRequest.doctorId` path in favour of
  finalize.
- Full **mock** implementations so the entire flow works in mock mode with no backend.

---

## 5. Frontend UI

**Routing (`src/app/router.tsx`):**
- New **full-screen focus route** `/consult/$bookingId` — deliberate exception to the
  slide-over-primary rule (needs two-pane live-preview width; the Figma prototype
  agrees). Excluded from PHI URL-encoding like existing clinical routes.

**Access paths ("easy access"):**
- `BookingsScreen` / `QueueRow` gain a permission-aware row action:
  - holder of `prescription.create` → **"Prescribe"** → `/consult/$bookingId`;
  - holder of `prescription.draft` only → **"Vitals / Prep"** → same route, intake-first.
- Driven by the resolved permission set — never a JSX role branch.

**Composer (`src/features/consult/ConsultScreen.tsx` + components):**
- Left pane (edit): **Vitals** → **Quick-start templates** → **Diagnosis chips** →
  **Medications** (search + structured dosing chips `1-0-1`/SOS/weekly, food timing,
  duration) → **Investigations chips** → **Advice chips + free text** → **Follow-up chips**.
- Right pane: **live Rx preview** (letterhead, reg no., signature block).
- **Section enablement by permission:**

  | Section | Editable when you hold… |
  |---|---|
  | Vitals, chief complaint, template pick | `prescription.draft` |
  | Diagnosis, medications, investigations, advice, follow-up | `prescription.create` |
  | **Finalize & send** | `prescription.create` |

  Non-`create` holders see clinical sections **disabled/read-only**, no Finalize.
- Footer differs by permission: `draft` → **Save draft**; `create` →
  **Save draft** + **Finalize & send** (Print / Save PDF / Send to WhatsApp).
- Inline **drug-alert** warnings before finalize (override needs reason).
- **Autosave** draft (debounced PATCH) so intake work is never lost before the doctor opens it.
- Skeleton / empty / error states; bilingual (`consult.*` keys + existing `clinical.*`),
  `.deva` on Hindi.

**Bugs to fix (in Phase A):** `IssuePrescriptionPanel.tsx` currently hardcodes
`doctorId: DOCTORS[0].id` and `bookingId: crypto.randomUUID()`. A real consultation
must bind the **actual booking** + the **server-derived logged-in doctor**.

---

## 6. Doctor-scoped views (dashboard + patient list)

**Behavior**
- **Overview = the doctor dashboard.** Same screen; when the caller is a doctor, the
  API returns only their confirmed appointments + derived patient set → Overview
  becomes their consult queue. Reception keeps the tenant-wide Overview.
- **Patient list, doctors only:** a doctor sees only patients with an **active
  confirmed booking with them**; reception sees the full list.
- **"Confirmed" = status `confirmed`/`checked_in`, slot date ≥ today.**

**Enforcement (no JSX role checks)** — server-side scoping keyed on caller read scope:
- Tenant-wide reader (`patient.read` / `booking.read`) → sees everything (reception).
- Self-scoped caller (`booking.read_self`, i.e. a doctor) → API resolves `doctor_id`
  from `docslot.doctors.user_id` (same identity as prescription authorship) and filters:
  - **Patients** → `DISTINCT patient_id` from bookings where
    `doctor_id = me AND status IN ('confirmed','checked_in') AND slot_date >= today`.
  - **Overview** stats/queue → same doctor + confirmed scope.
- Frontend renders whatever the scoped endpoint returns; scope derives from the token,
  not a client-set query param.

**Testing note:** dev login `priyanka@apollocare.in` is a *receptionist* (correctly
still sees everything). Verifying doctor-scoping needs a **doctor test user**
(`platform.users` row linked via `docslot.doctors.user_id`, holding `booking.read_self`).

---

## 7. Compliance & audit

- Hash-chained audit records **drafted-by** and **finalized-by** distinctly.
- Purpose-of-use declaration on opening the composer (reuse existing clinical gate);
  vitals included as PHI reads.
- Vitals write allowed for `draft` holders; clinical write + finalize `create`-only —
  enforced by permission, RLS, and the `finalized_by` CHECK (defense in depth).
- `drug_alerts` overrides require `override_reason`.
- Prescriptions remain soft-delete; amendments mint a superseding row (never overwrite).

---

## 8. Phasing & acceptance criteria

**Phase A — Composer + finalize (doctor value; low risk)**
- Full-screen route, structured meds/investigations/advice/follow-up, live preview,
  finalize → WhatsApp, server-derived author, both bugs fixed.
- ✅ Accept: a doctor opens a queued booking, builds a structured Rx, finalizes; PRX
  minted; author = logged-in doctor; reminder schedule generated; mock-mode fully works.

**Phase B — Role split (auditor-gated)**
- `vitals` column + `prescription.draft` permission + grants; intake writable by
  `draft` holders; clinical sections gated to `create`; autosave.
- ✅ Accept: a `draft`-only user records vitals + complaint (cannot finalize — button
  absent, API 403s); doctor opens pre-filled and signs; audit shows both actors.

**Phase C — Accelerators**
- Per-doctor templates/favourites (`docslot.rx_templates`), richer drug-alert checks,
  optional device vitals capture.

**Phase D — Doctor-scoped views (independent, low risk, no new permission)**
- Overview doctor-scoped + Patient list confirmed-only for doctors (§6) + doctor test user.
- ✅ Accept: doctor login sees only their active-confirmed patients + own Overview;
  reception unchanged.

**Suggested order:** D (quick visible win) or A (core composer) first — owner's call.
B depends on A. C last.

---

## 9. Open items / risks

- .NET service is a skeleton — API endpoints here are contract-first; scaffold
  database-first from the schema once building the backend wave.
- Need a doctor test user for Phases A/B/D verification.
- Structured `medications` migration: existing draft rows (none in prod yet) — fresh-DB
  friendly; add a display-string fallback for any legacy free-text meds.
