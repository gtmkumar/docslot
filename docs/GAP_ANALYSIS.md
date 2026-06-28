# DocSlot — Goal & Gap Analysis

| | |
|---|---|
| **Status** | Living document — first cut 2026-06-28 |
| **Method** | 7 parallel domain audits (booking, WhatsApp/identity, commission, RBAC/IAM, clinical/ABDM, AI, platform API) cross-referencing PRD ↔ schema ↔ .NET backend ↔ React SPA ↔ AI service, then synthesis. Evidence cited as `file:line`. |
| **Source of truth** | `docs/REQUIREMENTS.md` (FRs/NFRs), `database/` (113-table schema). |
| **Caveat** | The cross-cutting audit (analytics/settings/dashboard screen coverage, DPDP breach/crypto-erasure depth) did not finish structured output; its key items (RLS gaps, PHI-at-rest, audit) were captured via the clinical/AI/security findings. Analytics/Settings/Dashboard screens were not deeply audited and may hold additional mock-only gaps. |

## 1. North star — where we are vs the vision

DocSlot's **PostgreSQL schema is a genuinely mature, security-rich source of truth**, and the **.NET backend is far more built-out than the stale `CLAUDE.md` claims**. RBAC/IAM is essentially production-grade end-to-end (deny-wins overrides, resolve-once, RLS on all 7 RBAC tables, scoped/audited impersonation, SoD + escalation + last-admin guards, integration tests). The Platform API (OAuth2 client-credentials, scoped JWTs, DB-backed revocation, HMAC webhook signing) is a strong backend slice. The **happy-path WhatsApp booking works end-to-end** (HMAC webhook, idempotent inbound, atomic 5-min slot hold, lifecycle state machine, live-wired console).

**But the product is not yet shippable against any PRD acceptance gate:**
- **Phase-1 fails** — behalf/OTP consent is entirely unwired (every behalf booking persists as `self`), nothing generates `time_slots` from schedules (production reads an empty inventory table), and cancel/no-show leak slot capacity.
- **Phase-2 fails** — the commission money pipeline is structurally dead (nothing advances an attribution past `pending`, so every payout batch is empty and no broker can be paid), the direct-booking discount that funds the flywheel is never written, and a write endpoint is gated by a read permission.
- **Phase-3 fails** — ABDM is store-only: no gateway, no ABHA linking, no FHIR exchange, no consent creation.

Cross-cutting: large swaths of the polished React SPA are **mock-only despite live backend endpoints existing** (the entire clinical/ABDM surface, conversations, developer-portal writes, several IAM panels); the AI service is **functional but a standalone island** (not LangGraph; embeddings/PHI stored in plaintext; no .NET or frontend caller); and tenant-sensitive tables outside the RBAC/clinical core (`bookings`, `time_slots`, `patients`, `ai.*`) have **no RLS backstop**.

> **Net:** a strong schema and several near-complete backend slices, but the **consent, money-settlement, ABDM-integration, live-wiring, and PHI-encryption seams that define the product are missing or half-built.**

## 2. Domain snapshot

| Domain | Status | One-line readout |
|---|---|---|
| **RBAC / IAM / Navigation** | 🟢 strong | End-to-end + tested. Gaps: a few IAM read panels + `createRole` mock-only; tenant_type menus untested across types. |
| **Platform API (OAuth2/webhooks)** | 🟢 mostly-done | Strong auth/signing core. Gaps: no durable webhook drainer, event-id header unsent, per-day/burst limits unenforced, public data API stubbed, portal writes mock-only. |
| **Booking core** | 🟡 partial | Happy path solid. Gaps: reschedule unwired, cutoffs decorative, cancel/no-show leak capacity, **no `time_slots` generator**, pathology no-doctor leg missing, no `checked_in`, no RLS. |
| **WhatsApp / chat identity** | 🟡 partial | State machine + outbox solid & tested. **Behalf OTP consent missing**, hidden-partner nudge absent, bilingual effectively English-only, console view mock-only. Bug: every tenant greeted "Apollo Care". |
| **Commission / Care Partners** | 🟡 partial | Most mature non-DB domain. But **money pipeline is dead**: nothing advances attribution past `pending`, payouts always empty, gateway is a fake-UTR stub, direct-discount never written, write gated by read perm. |
| **Clinical records & ABDM** | 🟡 partial | Backend reads strong (PHI encryption, purpose-of-use, consent gate, RLS). **Entire clinical/ABDM frontend mock-only**; ABDM store-only; break-glass only logs, doesn't unlock reads. |
| **AI services** | 🟡 partial | Functional triage/RAG/OCR/no-show with PHI gating, but **not LangGraph**; standalone island; **embeddings/OCR text stored plaintext** in "encrypted" columns; no RLS on `ai.*`; eval loop dead; summarization & prescription-OCR missing. |

## 3. To implement (missing features) — prioritized

### 🔴 Blockers
| Item | Domain | Why | Effort |
|---|---|---|---|
| Wire the commission **earning→settlement pipeline** (booking-complete hook → `ApplyEarnedAsync`; settlement-window job → `ready_to_pay`; credit `broker_wallets`) | Commission | `ApplyEarnedAsync` has zero callers (`AttributionRepository.cs:27`); payout batches aggregate only `ready_to_pay` → always empty (`:96-104`); no broker can ever be paid. | L |
| Generate `docslot.time_slots` from `doctor_schedules`/`schedule_overrides` (DB fn or service + materializer) | Booking | Only test factories INSERT slots; in production the WhatsApp flow dead-ends at "no slots" and the create wizard has nothing to pick. | L |

### 🟠 High
| Item | Domain | Why | Effort |
|---|---|---|---|
| **Behalf-booking OTP consent flow** (consent fields + OTP generate/verify naming booker+relation; set `booked_by_type`/`behalf_*`/`patient_consent_status`) | WhatsApp | Schema supports it (`09_chat_identity.sql:78-98`) but handler drops the relation; every behalf booking persists as `self` with no consent — defeats DPDP guard + commission attribution. | L |
| Compute & write the **direct-booking discount** (`direct_discount_pct` of would-be commission) onto `bookings.direct_discount_inr` | Commission | Only ever SELECTed for the exclusivity check, never written (`AttributionRepository.cs:51`); the incentive doesn't exist. | M |
| Add **RLS** to `bookings`, `time_slots`, `slot_holds`, `doctors`, `patients`, `opd_tokens`, `booking_status_history`, and all `ai.*` tables | Security | Only the 5 clinical + 7 RBAC tables have RLS; booking & AI data are protected only by hand-written predicates over an RLS-bypassing owner conn — one missing predicate silently leaks cross-tenant PHI. | M |
| **Encrypt AI embeddings, `chunk_text`, raw OCR text** at rest (+ `encrypted_fields_registry` rows, populate `encryption_key_id`) | AI | Stored as raw bytes/plaintext with `encryption_key_id` NULL (`rag_repository.py:136-161`, `ocr_repository.py:34-84`) despite "encrypted" comments — PHI-at-rest / PII-inversion exposure. | M |
| **Reschedule end-to-end** (command/handler/endpoint → `Booking.MarkRescheduled`, slot re-hold/release, reason codes, live seam fn, wire dead button) | Booking | `MarkRescheduled` has zero callers; console renders a Reschedule row with no `onClick` (`ManageAppointmentPanel.tsx:100-104`). | L |
| Wire the **entire clinical/ABDM frontend** to the live API (prescriptions/lab-reports/history/abdm/consent in `real.ts` + seam) | Clinical | `patients/api.ts:12-25` imports every clinical fn from `@/lib/mock`; the built `ClinicalController` is unreachable from the SPA. | M |
| Wire **developer-portal writes + observability** to live; add backend webhook-delivery **list + retry** endpoints | Platform API | All mutating actions mock-only (`developers/api.ts:15-27`); webhook delivery list/retry have no backend at all. | M |
| **Durable webhook delivery** (background drainer re-picking `failed` by `next_retry_at`; send `X-DocSlot-Event-Id`) | Platform API | `WebhookPublisher.cs:84-92` always dead-letters as `abandoned`; pending index + `failed` branch are dead code; a subscriber down in the sync window loses the event permanently. | M |
| **Integrate the AI service** into product flows (.NET `AiClient` for triage/RAG/OCR/no-show; event triggers; frontend risk badges / OCR review queue / RAG Q&A) | AI | No .NET or frontend caller hits any AI endpoint — it's a standalone island. | L |
| Make **break-glass actually unlock** consent-denied clinical reads (bypass path through the read handlers, not just a log) | Clinical | `BreakGlassCommandHandler` only logs intent (`SecurityFeatures.cs:226-240`); reads hard-throw `ForbiddenException` with no bypass — FR-MED-03 unrealized. | M |
| Fix attribution **write authorization** + add the 3 real attribution paths (referral-link convert, portal booking, post-hoc OTP claim verify/deny) | Commission | `POST /attributions` (mints commission) gated by `commission.attribution.read` (`CommissionController.cs:101-105`); referral clicks never convert; post-hoc claims stick at `pending`. | L |

### 🟡 Medium
| Item | Domain | Effort |
|---|---|---|
| Enforce configurable **booking cutoffs** (`BookingCutoffHours`) in create/cancel/reschedule | Booking | M |
| Genuinely **bilingual WhatsApp** templates (detect/persist language, render all templates en/hi, use tenant `display_name` not "Apollo Care") | WhatsApp | L |
| **Hidden-partner heuristic** (nightly `distinct_patients_90d` job → `v_suspected_care_partners` → Care-Partner nudge, ≥3/90d, max 1/30d) | WhatsApp/Commission | M |
| **ABDM gateway integration** (ABHA linking+verify + `abha_number`, HFR/HPR registration, FHIR R4 push/fetch, consent-request handshake) | Clinical/ABDM | L (external-gated) |
| Medical-history create/update, prescription amend, lab-report **blob storage**, drug-alert generation | Clinical | M |
| **Pathology no-doctor-leg** booking (nullable `doctor_id` for tests, branch WA flow on tenant_type into test_catalog, relax validator) | Booking | L |
| **Broker self-service portal** frontend (wallet, referral-link gen, own attributions) against existing `/me/wallet`, `/me/links` | Commission | L |
| Payout **tax docs** (invoice numbering+PDF, Form 16A) + dispute **clawback/reversal** (apply `resolution_amount_adjustment_inr`, debit wallet) | Commission | M |
| Enforce **per-day/burst rate limits** + edge limiting + `requires_consent` gating; ship real `PublicApi` data surface | Platform API | L |
| Add booking **`checked_in`** lifecycle state (or reconcile with `opd_tokens`) | Booking | M |
| Wire console **conversation view** to a live read API | WhatsApp | M |

### 🟢 Low
| Item | Domain | Effort |
|---|---|---|
| Wire remaining mock-only IAM panels (`createRole` + the "why does X have Y" explainer reads) | RBAC | S |
| AI summarization + prescription OCR; persist RAG/OCR/prediction as `ai_workflows`+`agent_runs` (observability); implement `prescription_safety_check_v1` | AI | M |
| Backfill `ai.ai_predictions.actual_outcome` from realized booking status (close no-show eval loop) | AI | M |
| Commission rule-engine: parse `tiered_table`, full rule conditions via API, campaign bonuses + budget caps | Commission | M |

## 4. To fix (bugs / risks / half-built seams)

| # | Severity | Item | Evidence |
|---|---|---|---|
| 1 | 🟠 high | **Cancel/no-show permanently leak slot inventory** | `BookingActionCommandHandler` injects `ISlotHoldService` but never calls it; Cancel/MarkNoShow only flip status (`Booking.cs:108-126`); `time_slots.current_count` stays incremented → slot never re-bookable. |
| 2 | 🟠 high | **Webhook deliveries not durable** across the sync retry window | `WebhookPublisher.cs:84-92` always `abandoned`/`next_retry_at=null`; pending index + `failed` path dead. |
| 3 | 🟠 high | **AI "read" endpoint writes** under a read-only scope | `POST /rag/ask` auto-indexes (embedding INSERTs) with only `medical_history.read` (`routers/rag.py:149-150`). |
| 4 | 🟠 high | **AI RAG never verifies patient consent** before reading PHI | `medical_history_rag_v1` has a `verify_consent` node but `routers/rag.py` checks only an RBAC permission; `requires_consent` never enforced. |
| 5 | 🟡 med | No **(slot,doctor) consistency** check in CreateBooking | Hold verifies only `slot.tenant_id` (`SlotHoldService.cs:30-37`); a valid SlotId + unrelated DoctorId books another doctor's slot. |
| 6 | 🟡 med | No **doctor update/delete or schedule-management** endpoint | Perms exist (`03_docslot.sql:778-784`) but no endpoints — staff cannot define availability (compounds the empty-slots blocker). |
| 7 | 🟡 med | **₹100 payout minimum** applied net vs gross inconsistently | `PayoutCalculator` floors NET (`Payout.cs:23`); `v_ready_payouts` floors GROSS (`07:653`). |
| 8 | 🟡 med | No **hold sweeper** for expired `slot_holds` | Comment promises a sweeper (`03_docslot.sql:213`); only `OutboxDrainWorker` exists. |
| 9 | 🟡 med | Outbox **`processing` rows stranded** on shutdown (WA + webhook) | `OutboxDrainWorker.cs:116-119,155` — no reaper to requeue. |
| 10 | 🟡 med | **Stale AI model cache** never retrains | `model.py:47` cache populated once, cleared only by a test helper. |
| 11 | 🟡 med | Documented **super_admin direct-write RLS escalation** hole | `11_rbac_hardening.sql:1206-1216` (Finding 4) — safe only while all writes go through definer fns. |
| 12 | 🟡 med | AI **embeddings compared across incompatible vector spaces** | Per-row model/dim stored but retrieval dots against the current process embedder (`rag.py:97-125`). |
| 13 | 🟢 low | Frontend reports **`confirmed` for server-side `pending`** bookings | `real.ts:723`. |
| 14 | 🟢 low | Cancel-reason **contract mismatch** (server requires; UI says optional); no reason codes | `BookingActionCommand.cs:25-27` vs `ManageAppointmentPanel.tsx:116-117`. |
| 15 | 🟢 low | `wa_message_log.status` writes **non-canonical values** | `'received'`/`'queued'` vs documented `sent/delivered/read/failed` (`03_docslot.sql:633`). |
| 16 | 🟢 low | Relation picker exposes **3 of 5** schema relations | Missing `neighbour`/`other` (`09_chat_identity.sql:38`). |
| 17 | 🟢 low | Triage failed-run node attribution always `unknown` | `_node` never set (`triage.py:445`). |
| 18 | 🟢 low | OCR low-confidence not surfaced via status | `db_status` hardcoded `'extracted'` (`extractions.py:95`). |
| 19 | 🟢 low | **Stale comments** about wiring status (mislead maintainers) | `real.ts:681-685` claims slots/practitioners unwired though live. |
| 20 | 🟢 low | `GetMyMenusQuery` returns **403 for tenant-less** sessions | `MeController.cs:39-41` — should return an empty tree. |

## 5. Roadmap (sequenced to the PRD acceptance gates)

> **Progress** — Phase 0 ✅ done (2026-06-24) · Phase 1 ✅ done (2026-06-28, auditor PASS, 149/149) · Phase 2 CORE ✅ done (2026-06-28, auditor PASS-with-conditions, 163/163); Phase-2 remainder + Phases 3–4 pending.

**Phase 0 — Unblock the production data plane** ✅ DONE *(prerequisite)*
Make the live API usable at all: bookable inventory, slot reconciliation, DB-level isolation backstop.
`time_slots` generator + materializer · doctor schedule-management/update/delete endpoints · fix cancel/no-show capacity leak · hold sweeper · RLS on booking + `ai.*` tables · (slot,doctor) consistency check.

**Phase 1 — One WhatsApp booking end-to-end with OTP consent** ✅ DONE *(PRD gate 1)*
Delivered: behalf OTP consent flow (`docslot.booking_consent_otps`, salted-hash codes, attempt-limited, RLS; DPDP approval gate blocks un-consented behalf bookings) · reschedule end-to-end (terminate-old/mint-new with lineage) + cutoff enforcement (create+reschedule) · fully bilingual templates + per-contact language + tenant `display_name` (dropped hardcoded "Apollo Care") · `checked_in` lifecycle state · live conversation read API wired in the SPA · all 5 relations · canonical `wa_message_log` statuses (CHECK) · outbox `processing` reaper + consent-OTP expiry sweep · RLS added to `wa_message_log` + `outbox_messages` (drain via SECURITY DEFINER fns) · OTP code redacted from journal + scrubbed from queue post-send (auditor F1) · check-in/reschedule/consent surfaced in the SPA with consent-gated Approve.

**Phase 2 — Commission attribution + payout dry-run** *(PRD gate 2)* — CORE ✅ DONE (auditor PASS-with-conditions)
Delivered (the money pipeline is alive end-to-end): booking-complete hook earns attributions (pending→earned + wallet) · settlement-window job earned→ready_to_pay (SECURITY DEFINER) · payout batches now non-empty → approve → execute via `IPayoutGateway`/`StubPayoutGateway` **honest dry-run** (`DRYRUN-` ref + `payment_gateway='stub_dryrun'`; gateway-failure→'failed', no wallet move) · concurrency-safe execute (atomic approved→processing claim) · cancel/no-show reversal + dispute tenant-wins clawback (reverse + wallet debit) · attribution write authz read→`commission.attribution.override` · `tiered_table` parsing · ₹100 floor reconciled to GROSS (matches `v_ready_payouts`) · RLS on the 6 tenant-scoped commission tables.
REMAINDER (Phase-2 follow-on): compute/write the direct-booking discount onto bookings · the 3 real attribution paths (referral-link click→convert · broker-portal booking · post-hoc OTP claim verify/deny) · hidden-partner nightly job + Care-Partner nudge · campaign bonuses · invoice numbering + Form 16A PDFs · broker self-service portal (frontend) + admin UI for earned/settled/reversed states · gateway-go-live follow-ups (auditor F2: move gateway call outside the UoW txn; F3: reverse phantom `pending_inr` on patient_denied/no_response) — both before wiring a REAL payout adapter.

**Phase 3 — ABDM sandbox + clinical live-wiring + PHI hardening** *(PRD gate 3)*
Wire clinical/ABDM frontend to live API · break-glass that bypasses consent denial · ABDM gateway (ABHA/HFR/HPR/FHIR/consent) · medical-history CRUD + prescription amend + lab-report blobs + drug alerts · encrypt AI embeddings/OCR at rest · fix RAG consent + read-that-writes + cross-space embedding + stale model cache.

**Phase 4 — Platform/API hardening + AI integration + portal live-wiring**
Durable webhook drainer + event-id header + emit real domain events · per-day/burst limits + edge limiting + consent gating + real PublicApi · wire developer-portal writes + deliveries list/retry · integrate AI into .NET + frontend · no-show eval backfill + summarization + prescription OCR + safety-check + observability · wire remaining IAM panels + tenant_type menu tests.

## 6. Top risks

1. **The commission value prop is financially non-functional today** — nothing advances an attribution past `pending`, and payout execution mints a **fake UTR while reporting "paid"** (`PayoutFeatures.cs:117-119`). Shipping this risks reporting payouts that never happened. Hard blocker, not polish.
2. **Mock/live seam gaps make the product look more complete than it is** — clinical/ABDM, conversations, developer-portal writes, broker portal, several IAM panels are mock-only despite live endpoints. **Gate acceptance on `VITE_USE_REAL_API=true`.**
3. **PHI exposure on two fronts** — AI embeddings/`chunk_text`/OCR text stored plaintext in "encrypted" columns; booking/patient/`ai.*` tables have no RLS. A single missing `tenant_id` predicate is a cross-tenant PHI leak. DPDP liabilities the auditor would veto for production.
4. **Behalf bookings silently persist as `self` with no consent** — defeats the DPDP fake-patient guard the schema was built to close, and corrupts commission attribution keyed on `booked_by_type`.
5. **ABDM is store-only** — Phase-3 sandbox certification is effectively unstarted and is long-pole, externally-gated work (ABHA OAuth, HFR/HPR, FHIR conformance). Schedule early.
6. **"LangGraph" triage is a hand-rolled rule engine** (no langgraph/langchain dependency). If the contract specifies LangGraph, FR-AI-01 is unmet despite a functional service.
7. **Webhook + both outbox workers have silent message-loss paths** (always-abandon, no `processing` reaper) — integrators and outbound WhatsApp can lose events with no operator signal.
8. **`CLAUDE.md` is stale** — it understates maturity (claims only two backend library projects). Update it before onboarding contributors so they don't rebuild working slices.
