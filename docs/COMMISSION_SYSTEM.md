# Commission & Broker System

> Formalizes the informal Indian healthcare referral economy inside DocSlot. Facilities pay registered marketing partners ("brokers") for patient bookings they facilitate — with attribution, tax compliance, and PCPNDT safety built in at the database level.
>
> **Canonical schema**: `database/07_commission_broker.sql` (10 tables in `commission.*`). This document is the narrative companion; where they conflict, the SQL wins.

## Why this exists

60-70% of Tier-2/3 Indian healthcare bookings already involve a human facilitator — medical reps, corporate HR managers, insurance panel coordinators, local aggregator agents, hotel concierges. Today this economy runs on WhatsApp groups and cash envelopes. DocSlot makes it legitimate: transparent commissions, automated attribution, tax-compliant payouts, and an auditable trail for the facility's books.

**Business leverage**: hospitals get patient acquisition at 5-15% commission (vs ~25% for marketing agencies), brokers get reliable payouts with tax documents, and DocSlot can take a platform cut of every transaction — a revenue stream layered on top of subscriptions. Network effects: more brokers → more bookings → facilities stay → more brokers register.

## Legal positioning (read before changing anything)

This is the most important section of this document.

- The commission is a **marketing/facilitation fee paid by the FACILITY to a registered marketing partner**.
- It is **NOT** a doctor-to-doctor referral fee. MCI Code of Ethics Regulation 6.4 prohibits doctors from paying or receiving commission for referring patients to other doctors. DocSlot's structure keeps doctors entirely out of the money flow — the tenant (hospital/lab/clinic as a business entity) pays the broker.
- **PCPNDT Act**: payments connected to gender-determination referrals are criminal. This is enforced at the database level with CHECK constraints that cannot be bypassed by application code:
  - `commission.brokers.can_refer_pndt` is CHECK-forced to `false`
  - `commission.commission_rules.excludes_pndt` is CHECK-forced to `true`
- **DPDP Act 2023**: brokers see minimal patient data (first name + masked phone). PAN numbers are encrypted at rest (`platform.encrypted_fields_registry`, legal_basis = `legal_obligation` per the Income Tax Act).

This is the same legal pattern used by Practo, Lybrate, and CallHealth.

## The actors

| Broker type | Who they are | Typical economics |
|---|---|---|
| `medical_rep` | Pharma sales reps with doctor relationships | ₹500-2,000 per consultation referral |
| `corporate_hr` | HR managers booking employee checkups in bulk | Package deals |
| `insurance_panel` | Panel coordinators steering cashless patients | High volume |
| `aggregator_agent` | Local healthcare navigators in Tier-2/3 cities | The "WhatsApp number you call" |
| `community_worker` | ASHA/ANM workers (sensitive — extra approval required) | Small amounts |
| `hotel_concierge` | Medical tourism referrers | 10-15% norm |
| `individual` / `platform_partner` | Everyone else / strategic partners | Varies |

## The 10 tables at a glance

| Table | Role |
|---|---|
| `brokers` | Registered partners: phone-canonical identity, PAN/Aadhaar KYC, tier (basic→platinum), banking, blacklist |
| `broker_tenant_links` | Which brokers work with which facilities; per-tenant status + denormalized stats |
| `referral_links` | Trackable short codes (`BRK-RAVI-1234`) for WhatsApp shares, QR posters, campaigns |
| `referral_clicks` | Click log with hashed IP, session token, conversion tracking |
| `commission_rules` | Tenant rate cards: flat / percentage / tiered, with caps, floors, priorities, monthly anti-runaway limits |
| `attributions` | **The core ledger** — one row per (broker, booking) with source, verification status, fraud score |
| `payouts` | Batch settlement: gross → TDS (5% u/s 194H) → GST → net; invoice number; UPI/bank execution |
| `broker_wallets` | Materialized real-time balance for the Broker Portal |
| `attribution_disputes` | Formal disagreement workflow (incorrect attribution, duplicates, fraud) |
| `broker_campaigns` | Time-boxed bonus campaigns with budgets |

## Three attribution mechanisms

A booking is credited to a broker if any one of these matches:

1. **Referral link** — broker shares `wa.me/+91...?ref=BRK-1234`; patient clicks; code rides the session. Verification: `auto_verified` (deterministic).
2. **Broker portal booking** — broker logs in and books on the patient's behalf; WhatsApp OTP to the patient confirms consent. Verification: instant.
3. **Post-hoc claim** — broker claims within 48 hours of a booking; the patient receives "Did [BROKER] refer you?" on WhatsApp and must confirm within 24 hours. Verification: `patient_confirmed` or `patient_denied`.

A booking can carry only one attribution per broker (`UNIQUE(booking_id, broker_id)`), and disputes resolve conflicts between brokers.

## Commission lifecycle

```
Booking created          → attribution row, commission_status = 'pending'
Visit completed          → 'earned' (earned_at set)
Settlement window passes → 'ready_to_pay'
Monthly payout batch     → 'paid' (payout_id, UTR reference, invoice)
Patient refunded         → 'reversed' (clawback)
```

Payout math: `net = gross − TDS(5%)` (+18% GST added if the broker is GST-registered). Minimum payout threshold ₹100 (see `v_ready_payouts`). Form 16A TDS certificates issued quarterly.

## Compliance & fraud controls

- **PAN required** above ₹15k/year earnings; **GST registration** above ₹20L/year.
- `fraud_score` (0-1) with flags: `repeat_phone`, `rapid_burst`, `self_referral`. Flagged attributions (>0.5) indexed for review.
- Blacklisting is permanent and platform-level (`commission.broker.blacklist` is a platform-scoped, dangerous permission).
- Every manual attribution override requires a reason and is audited.

## RBAC integration

19 permissions registered under the `commission.*` namespace, plus a new self-service `broker` role (`read_self`, `update_self`, `generate_link_self`). Role assignments:

- `tenant_owner` — everything tenant-scoped including payout approval
- `tenant_admin` — everything except `payouts.execute` and `broker.blacklist`
- `tenant_staff` — read + invite + raise disputes
- `broker` — own profile, own links, own wallet only

The **Brokers** menu and **Settings → Broker Commission** menu are gated by `commission.broker.read` and `commission.rules.read` respectively (see RBAC_NAVIGATION.md).

## What's schema-complete vs still to build

✅ In the database: all tables, constraints, permissions, views (`v_applicable_rules`, `v_broker_leaderboard`, `v_ready_payouts`), PCPNDT enforcement, encrypted-field registration — verified end-to-end against PostgreSQL 16.14 (valid broker insert succeeds; PNDT-flagged insert is rejected by constraint).

⏭️ At implementation time (application code):
- Attribution engine (rule matching by priority, commission calculation)
- WhatsApp OTP confirmation flow for post-hoc claims
- Wallet-updating triggers or service logic on attribution/payout transitions
- Payout batch job + payment gateway integration (Razorpay X / Cashfree)
- Invoice PDF + Form 16A generation
- Broker Portal UI (React) and fraud-scoring job

## Key design decisions

See ADR-011 in `.agents/memory/decisions.md`. Source of truth hierarchy: `07_commission_broker.sql` > this document > anything in PRODUCTION_SPEC.md.

## Chat identity & the direct-booking discount (09_chat_identity.sql)

**Customer-facing name for brokers: "Care Partner" (केयर पार्टनर)** — deliberately NOT "Referral Partner", since the word "referral" undermines the MCI 6.4 marketing-fee positioning above.

**The identity flow**: fresh WhatsApp numbers are asked "Who is this appointment for?" (Myself / Someone else → relation picker: Family / Friend / Neighbour / Care Partner / Other). The answer is remembered in `docslot.wa_contact_profiles`; returning numbers get a one-tap confirm defaulting to their last choice (numbers are shared/recycled in India — remember the default, always confirm cheaply).

**Behalf-booking consent (closes the fake-patient + DPDP gap)**: every behalf booking sends the patient a WhatsApp OTP message that names the booker AND the claimed relation ("Your brother Ravi is booking..."). Bookings carry `patient_consent_status`; the claimed-relation display lets patients dispute falsified relations.

**Direct-booking discount**: patients who book directly (no attribution) receive `direct_discount_pct` (default 50%) of the would-be commission as a discount, funded from the facility's commission pool — facility saves the remainder. Flywheel: Care Partners do acquisition (first booking, full commission, optionally `first_booking_only`), the discount converts patients to the direct channel for repeats, which is where the app-install nudge lands.

**Anti-double-dip (DB-enforced)**: a trigger on `commission.attributions` rejects any attribution on a booking carrying `direct_discount_inr > 0`. Accepting the discount IS the patient's declaration that no one referred them — verified by test (post-hoc claim on discounted booking → blocked; attribution on clean booking → allowed).

**Hidden-broker conversion**: `docslot.v_suspected_care_partners` surfaces numbers booking for 3+ distinct patients in 90 days without registration — they get a "become a Care Partner and earn" nudge (carrot, not stick; max one per 30 days).
