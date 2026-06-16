# DocSlot — Product Requirements Document (PRD)

| | |
|---|---|
| **Status** | Approved — schema complete, implementation pending |
| **Owner** | Goutam Roy |
| **Version** | 1.0 (2026-06-10) |
| **Detailed spec** | PRODUCTION_SPEC.md (260 KB, appendices A-Y) — this PRD is the contract-level view |
| **Traceability** | Each requirement cites its canonical source (SQL file / spec appendix / ADR) |

## 1. Product overview

DocSlot is a **WhatsApp-first appointment booking SaaS for the Indian healthcare market**. One configurable codebase serves three tenant types — `individual_doctor`, `hospital`, `pathology_lab` — with behavior driven by the tenant_type enum (ADR-001). Two services: DocSlot.API (.NET 10, system of record) and DocSlot.AI (Python 3.12 FastAPI). Frontend: React 19.2 SPA (ADR-015). Database: PostgreSQL 16, 113 tables across 8 schemas.

**Primary thesis**: Indian patients live on WhatsApp; doctors' front desks run on phone calls and paper. DocSlot moves the entire booking lifecycle into WhatsApp for patients while giving staff a modern web console.

## 2. Actors

| Actor | Description | Interface |
|---|---|---|
| Patient | Books, reschedules, receives prescriptions/reports | WhatsApp only (app optional later) |
| Tenant owner / admin / staff | Runs the facility | React web console |
| Doctor | Manages schedule, completes visits, prescribes | Web console (+ WhatsApp notifications) |
| Care Partner (broker) | Facilitates bookings for commission | WhatsApp + Broker Portal |
| Platform admin | Operates the SaaS | Web console (platform scope) |
| Third-party developer | Integrates via Platform API | REST API (OAuth) |

## 3. Functional requirements

### FR-BOOK — Booking core (source: 03_docslot.sql, Spec App. A-C)
- FR-BOOK-01: Patient books via WhatsApp conversation: specialty/doctor → slot → confirm, ≤6 message exchanges.
- FR-BOOK-02: Slot inventory per doctor with date/time, capacity, hold-on-selection (optimistic hold TTL 5 min).
- FR-BOOK-03: Reschedule/cancel by patient (WhatsApp) and staff (console) with reason codes and configurable cutoffs.
- FR-BOOK-04: Booking lifecycle: requested → confirmed → checked_in → completed / cancelled / no_show, append-only status events.
- FR-BOOK-05: Multi-facility tenants route bookings per facility; pathology tenants book tests (no doctor leg) — behavior switches on tenant_type.

### FR-CHAT — WhatsApp & chat identity (source: 03 + 09_chat_identity.sql, ADR-014)
- FR-CHAT-01: Fresh numbers asked "who is this for?" (self/behalf → relation picker incl. Care Partner); answer remembered in `wa_contact_profiles`; returning numbers get one-tap confirm defaulting to last choice.
- FR-CHAT-02: Behalf bookings require patient OTP consent naming booker + claimed relation; consent status on booking.
- FR-CHAT-03: Hidden-partner heuristic: ≥3 distinct patients/90 days without registration → Care Partner nudge (max 1/30 days).
- FR-CHAT-04: All templates bilingual (en/hi); language preference persisted per contact.
- FR-CHAT-05: Outbound messaging via outbox pattern with retry; full message log.

### FR-COMM — Commission & Care Partners (source: 07_commission_broker.sql, COMMISSION_SYSTEM.md, ADR-011/014)
- FR-COMM-01: Broker registration with phone-canonical identity, PAN KYC (mandatory >₹15k/yr), tiering basic→platinum.
- FR-COMM-02: Three attribution mechanisms: referral link, broker-portal booking, post-hoc claim w/ patient OTP; one attribution per (booking, broker).
- FR-COMM-03: Tenant-configurable rules: flat/percentage/tiered, caps, floors, monthly per-broker cap, `first_booking_only`.
- FR-COMM-04: Direct-booking discount = `direct_discount_pct` (default 50%) of would-be commission; **mutually exclusive with attribution (DB trigger)**.
- FR-COMM-05: Payout batches with auto-TDS 5% u/s 194H, GST handling, invoice + Form 16A; UPI/bank execution; ₹100 minimum.
- FR-COMM-06: PCPNDT exclusion CHECK-enforced; commission UI label is "Care Partner", never "Referral Partner".
- FR-COMM-07: Dispute workflow with evidence, resolution outcomes, clawback adjustments.

### FR-RBAC — Identity & access (source: 01 + 08_rbac_navigation.sql, RBAC_NAVIGATION.md, ADR-002/012/013)
- FR-RBAC-01: Multi-tenant RBAC: user → tenant → role; product-namespaced permissions with scope levels.
- FR-RBAC-02: Per-user grant/deny overrides; deny wins; time-boxable; reason mandatory.
- FR-RBAC-03: Backend-driven navigation: `get_user_menus()` filtered by permission AND tenant_type; frontend renders the returned tree only.
- FR-RBAC-04: Authorization middleware resolves the effective permission set once per request (`resolve_user_permissions()`), checks in memory.
- FR-RBAC-05: Admin can answer "why does user X have permission Y" via `v_user_effective_permissions` source column.

### FR-MED — Clinical records (source: 03 + 05, Spec App. F-H)
- FR-MED-01: Prescriptions, lab reports, medical history per patient; RLS-isolated by tenant; purpose-of-use declared on access.
- FR-MED-02: ABDM integration: ABHA linking, HFR/HPR registration, FHIR R4 records exchange.
- FR-MED-03: Break-glass emergency access with mandatory justification + audit alert.

### FR-AI — AI services (source: 06_ai_services.sql, AI_ARCHITECTURE.md)
- FR-AI-01: LangGraph workflows for triage, summarization, OCR extraction of prescriptions/reports, no-show prediction.
- FR-AI-02: Encrypted embeddings (ADR-004); prediction outcomes recorded for evaluation.
- FR-AI-03: AI never sends patient-facing messages without a template approved by the tenant.

### FR-PLAT — Platform API (source: 02_platform_api.sql)
- FR-PLAT-01: OAuth 2.0 client credentials with scoped JWTs; webhook subscriptions with HMAC signatures and retry.

## 4. Non-functional requirements

| ID | Requirement | Target / Source |
|---|---|---|
| NFR-PERF-01 | Permission resolution | resolve-once-per-request pattern; measured 0.31 ms menu resolution, 0.035 ms single check (ADR-013) |
| NFR-PERF-02 | WhatsApp round-trip (webhook→reply) | p95 < 3 s |
| NFR-PERF-03 | Console route load (warm) | p95 < 1.5 s on 4G |
| NFR-SEC-01 | DPDP Act 2023 compliance | encryption registry, consent records, cryptographic erasure (ADR-005), breach reporting tables (05) |
| NFR-SEC-02 | Tenant isolation | RLS on sensitive tables + tenant_id scoping everywhere |
| NFR-SEC-03 | Tamper-evident audit | hash-chained audit log (05) |
| NFR-COMP-01 | PCPNDT, MCI 6.4, 194H TDS | DB CHECK constraints + marketing-fee positioning + auto-TDS |
| NFR-AVAIL-01 | Availability | 99.5% (single VPS phase, ADR-009); upgrade path documented in DEPLOYMENT.md |
| NFR-SCALE-01 | Capacity | 1k tenants / 100k bookings/month on Hetzner CX22 baseline; PostgreSQL partitioning plan for message/audit tables |
| NFR-I18N-01 | Bilingual en/hi everywhere | WhatsApp templates, menu labels, console strings |
| NFR-A11Y-01 | WCAG 2.1 AA on console | Radix + checklist in REACT_SKILL.md |
| NFR-COST-01 | Run cost | ≤ ₹500/month pre-revenue (ADR-009) |

## 5. Out of scope (v1)

Native mobile apps; payments/checkout (UPI deep-links only); telemedicine video; RuralReach/SafeHer/GenericFirst (schemas exist, products deferred); rider cash handling; multi-country.

## 6. Acceptance gates

Schema: ✅ 113 tables validated on PostgreSQL 16.14 (bundle SUCCESS). Implementation gates per phase: Phase-1 vertical slice = one tenant, one doctor, one WhatsApp booking end-to-end with OTP consent; Phase-2 = commission attribution + payout dry-run; Phase-3 = ABDM sandbox certification.
