---
name: broker-portal-campaigns-form16a
description: Phase-2 commission additions — Care Partner self-service portal (/portal), Campaigns admin tab, Form 16A (TDS) actions on PayoutsTab. DTO shapes, IDOR/PHI rules, seam wiring.
metadata:
  type: project
---

Phase-2 broker-portal-frontend slice extended Slice 07 commission with THREE deliverables (all wired through the real/mock seam in `lib/backend/index.ts`, DTOs verified against `mediq.SharedDataModel/Docslot/Commission/CommissionDtos.cs` + `CommissionController.cs`). Builds green (tsc + vite). Branch `feat/phase2-broker-portal-frontend`.

## 1. Care Partner self-service PORTAL (`features/portal/`, route `/portal`)
NEW feature folder (the deferred deliverable from [[commission-console]]). `PortalScreen` = WalletSummary + ReferralLinks + a book-on-behalf CTA. `api.ts` hooks: `useBrokerWallet`/`useReferralLinks`/`useCreateReferralLink`/`useCreatePortalBooking` + `usePortalPractitioners`/`usePortalSlots` (the doctor/slot pickers REUSE seam `listPractitioners`/`listSlots` — NOT bookings-feature internals, honoring the cross-feature rule).
- **IDOR-safe**: portal endpoints `GET/POST /commission/me/wallet|links|bookings` carry NO id in the path — server resolves broker_id from the JWT `broker_id` claim. Contracts mirror BrokerWalletDto / ReferralLinkDto / CreateReferralLinkRequest / CreateBrokerBookingRequest / BrokerBookingResult.
- **ReferralLinkDto has no campaignName** → real adapter fills `campaignName:null` before zod-parse (schema models it nullable for the create-echo). createReferralLink sends `{tenantId:null,targetDoctorId:null,campaignName}` (server-resolved context).
- **book-on-behalf** (`BookOnBehalfPanel`, payloadless URL-addressable `?panel=bookOnBehalf`): doctor select → slot select (sends `slot.slotId ?? slot.time`, IST-explicit via `istSlot`) + patient phone/name/age/gender/complaint. Result status `awaiting_patient_consent` → a WhatsApp-OTP consent banner (whatsapp-soft token) is the load-bearing UX, surfaced in panel `description` + an in-body banner + the success toast. NO patient PHI in the URL (collected inside the panel only).
- Perm gates: wallet/links read `commission.broker.read_self`; generate link `commission.broker.generate_link_self`; book-on-behalf `commission.broker.create_booking_self`. All 3 added to the demo `SIGNED_IN_PERMISSIONS` seed.
- **NAV GAP (flagged to orchestrator)**: mocked a nav node `{key:'partner_portal', route:'/portal', icon:'wallet'}` in `lib/mock/index.ts` HOSPITAL_MENUS (sortOrder 11) — `08_rbac_navigation.sql` needs a real navigation_menus row (tenant_type-scoped to partner-facing tenants) + menu→`commission.broker.read_self` map. New `wallet` icon in icons.tsx. Route added to router (`/portal` → lazy PortalScreen) so a future live menu node resolves without 404.

## 2. CAMPAIGNS admin tab (`features/commission/components/CampaignsTab.tsx` + `CreateCampaignPanel.tsx`)
6th tab in CarePartnersScreen (`commission.tabCampaigns`). Mirrors CampaignDto `{campaignId,campaignName,bonusType,bonusValue,isActive,totalBudgetInr,spentSoFarInr}`. Each row shows **spent_so_far vs total_budget as a ProgressBar** (colorKey escalates primary→warn→accent at 70/90%). Create slide-over (`?panel=createCampaign`, payloadless) offers ONLY 2 bonus kinds (`flat_bonus_per_booking`,`percentage_multiplier`) — tier_upgrade deliberately NOT offered. Dates: a YYYY-MM-DD input → ISO at start-of-day IST (`+05:30`). Gated `commission.campaign.manage` (already in demo seed). Hooks `useCampaigns`/`useCreateCampaign`; create returns `{id}` (list refetches). real.createCampaign tolerates bare-string OR `{campaignId}` return.

## 3. FORM 16A (TDS 194H) on PayoutsTab — for `paid` payouts only
Gated `commission.tds.issue` (added to demo seed). Two-step: **Issue Form 16A** (`POST /commission/payouts/{id}/form-16a` → Form16ACertificateDto) → then **View certificate**. Cert held in PayoutCard LOCAL state (carries PAN **last-4 only** — safe to show as `PAN ••••{{last4}}`); shows invoiceNumber + a **PROVISIONAL-until-TRACES** note + TDS line.
- **PHI-CRITICAL — full PAN**: the document (`GET /commission/payouts/{id}/form-16a/document`, text/html, FULL PAN) is opened via `openForm16ADocument(payoutId)` in the seam. It can't go through `apiFetch` (JSON-only) AND a bare `window.open` wouldn't carry the Bearer/tenant headers → so the helper FETCHES with auth headers into a transient Blob, opens an object URL in a new tab, and revokes after 60s. The full-PAN HTML NEVER enters React state, the query cache, or logs (server logs the access to key_usage_log). Mock parity renders a clearly-synthetic placeholder blob (no real PAN).

## Seam additions (`lib/backend/index.ts`, both real+mock)
`listCampaigns`,`createCampaign`,`issueForm16A`,`getForm16ADocumentUrl`,`openForm16ADocument`,`getBrokerWallet`,`listReferralLinks`,`createReferralLink`,`createPortalBooking`. New contracts in `lib/mock/contracts.ts`: Campaign/CreateCampaignRequest (CampaignBonusType enum), Form16ACertificate/Form16AStatus, BrokerWallet, ReferralLink/CreateReferralLinkRequest, BrokerPortalBookingRequest/BrokerBookingResult (BrokerGender enum). Mock seed in `lib/mock/commission.ts`.

## i18n
New keys (en+hi parity, compiler-enforced): `commission.tabCampaigns`, `commission.campaigns.*`, `commission.payouts.form16a.*`, `commission.validation.{campaignName,startsAt,endsAt}`, and the whole `portal.*` namespace (wallet/links/behalf). "Care Partner" terminology preserved (never "broker" in UI copy).

See [[commission-console]], [[commission-live-wiring]], [[live-api-seam]], [[contract-surface]].
