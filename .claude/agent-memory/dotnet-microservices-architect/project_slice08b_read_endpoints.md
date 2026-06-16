---
name: slice08b-read-endpoints
description: Slice 08b read-list GET endpoints backing the FE mocks (api-requests/security-console/clinical-lists/attributions). DTOs match frontend/src/lib/mock/contracts.ts. PHI-masking + Npgsql gotchas.
metadata:
  type: project
---

Slice 08b added the read-list GET endpoints the React SPA was built against (mock-first). COMPLETE, 47/47 tests green across 3 parallel runs, no schema edits, no MediatR, app still docslot_app.

**Why:** the FE built every list/console screen to spec against zod contracts in `frontend/src/lib/mock/contracts.ts`. Goal: make FE reconciliation a one-line `queryFn` swap, so .NET DTOs must serialize (camelCase) 1:1 to those zod schemas. That file is the authoritative DTO reference for these endpoints — read it before changing any read DTO.

**How to apply / what was built:**
- Endpoints: `GET /api/v1/api-requests` (paginated page wrapper); security console `GET /security/{audit-chain/anchors,dpdp/requests,breaches,review-queue,keys,deletion-certificates}`; clinical `GET /patients/{id}/{lab-reports,abdm-records,consent}` + `POST /patients/{id}/lab-reports/{reportId}/deliver`; commission `GET /commission/attributions`.
- Pattern reused (DRY): query record + handler in Application/Features (auto-registered by assembly scan), a read service/repo method in Infrastructure, controller route gated by `[RequirePermission(...)]`. Clinical lists reuse the 03b purpose-of-use + consent gates (headers-only, no decrypt). See [[slice03b-clinical-phi]] for the encrypted-read gotchas.

**PHI/secret rules enforced on every list:**
- `PhoneMasker.Mask` (internal to Infrastructure, namespace mediq.Infrastructure.Docslot) applied to ALL subject/broker/patient phones at the infra seam — raw phone never serialised.
- api_requests responses carry metadata ONLY (no ip_address/user_agent/bodies — the DTO has no such field). Security key rows carry NO key material. Review-queue actor = initials only.
- **PHI BUG FIXED:** `BrokerDto.Phone` (raw) → `BrokerDto.MaskedPhone` — the Care-Partner list was leaking the raw broker phone. The read site now masks. Also enriched PayoutDto+BrokerName, DisputeDto+BookingRef/BrokerName, added AttributionListItemDto (first-name + masked-phone).

**GOTCHAS (load-bearing):**
- Npgsql 500 on `(@p IS NULL OR col = @p)` when @p is an untyped DBNull → MUST cast in SQL: `@p::uuid` / `@p::timestamptz`. Bit the api-requests filter.
- `docslot.patients` phone column is `phone_number` (NOT `phone`). Bit the attribution + consent-context joins.
- Commission GET lists (brokers/rules/payouts/disputes/campaigns) ALREADY existed before 08b — the known-issues backlog note claiming "no GET lists" was stale. Only `GET /attributions` was genuinely missing.
- `lastVerifiedAt` on the audit-chain verify result has no native timestamp → surfaced as MAX(audit_anchors.anchored_at).

**Still-open drift (flagged):** `PrescriptionListItemDto` returns `doctorId` but FE wants `doctorName` (pre-existing 03b endpoint, out of 08b scope). Invoice/Form-16A PDF download deferred (no stub).
