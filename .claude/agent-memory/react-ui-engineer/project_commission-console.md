---
name: commission-console
description: Slice 07 frontend — commission / Care Partners admin console (/care-partners). Care Partner terminology (MCI 6.4), PCPNDT-enforced, approve≠execute payouts, PAN/PHI safety.
metadata:
  type: project
---

Slice 07 (commission_broker) frontend shipped the commission / Care Partners admin console at `/care-partners` (staff/admin view). Mirrors `mediq.SharedDataModel/Docslot/Commission/CommissionDtos.cs` (camelCase) + the commission.* SQL enums. Source of truth: `docs/COMMISSION_SYSTEM.md`.

## LEGAL/SAFETY (load-bearing — verified by audit greps)
- **"Care Partner" (केयर पार्टनर)** is the customer-facing term EVERYWHERE in UI strings (MCI 6.4). Code/types/DTOs say broker; i18n labels NEVER say "broker"/"referral partner" (grep-clean). Menu node key `care_partners`, route `/care-partners`, icon `handshake`.
- **PCPNDT**: every rule's `excludesPndt` is `z.literal(true)` — shown as an ENFORCED `PndtBadge` (a guarantee, never a toggle) on each rule row + a guarantee banner in ManageBrokerPanel + a non-disableable note in CommissionRulePanel.
- **PAN**: NO full PAN anywhere. It's a masked-style input in RegisterBrokerPanel (uppercase, maxLength 10, validated `ABCDE1234F`), stored encrypted server-side, and the register RESULT returns no PAN → no show-once needed. BrokerDto carries only `panVerified` (boolean). Grep-clean: PAN never rendered.
- **DPDP/PHI**: Care Partner phone = MASKED in lists; patient in attribution context = first name + masked phone ONLY (no last name/email/full phone). No commission refs in the command palette.

## APPROVE ≠ EXECUTE (the critical money split — don't collapse it)
Payouts are a two-step, independently-gated workflow in `PayoutsTab`:
- status `pending` → **Approve** button, gated `commission.payouts.approve` (→ status `approved`).
- status `approved` → shows "Approved · awaiting execution" + a SEPARATE **Execute** button, gated `commission.payouts.execute` (→ `processing`/`paid`).
Each gates on its OWN key (lines ~110 approve / ~119 execute) — removing either key hides exactly that step. Hooks `useApprovePayout`/`useExecutePayout` are separate mutations; both carry a stable Idempotency-Key. Mock advances status accordingly so the split works end to end. The breakdown (gross → TDS 5% 194H → GST → net, ₹100 min) is shown in an expandable panel.

## Screens (`features/commission/`, lazy route)
`CarePartnersScreen` — Radix Tabs: Care Partners | Attribution ledger | Commission rules | Payouts | Disputes. `api.ts` has all hooks. Tabs in `components/`: `PartnersTab`, `AttributionsTab` (fraud flag when fraudScore>0.5 + raise-dispute), `RulesTab` (PndtBadge per row), `PayoutsTab` (approve≠execute), `DisputesTab`. Shared `CommissionBadge`/`PndtBadge`. Panels: `RegisterBrokerPanel`, `ManageBrokerPanel` (suspend/activate + DANGEROUS blacklist with inline reason+confirmation, gated `commission.broker.blacklist`), `CommissionRulePanel`, `RaiseDisputePanel`, `ResolveDisputePanel`. All 5 panels URL-addressable (no PHI/secret payload → in `panelToSearch`/`searchToPanel`, NOT in TRANSIENT_SET).

## Permission gates (all real commission.* keys, verified in 07_commission_broker.sql)
register `commission.broker.invite`; suspend/activate `commission.broker.suspend`/`.activate`; blacklist `commission.broker.blacklist`; raise/resolve dispute `commission.dispute.raise`/`.resolve`; create rule `commission.rules.create`; payout approve/execute `commission.payouts.approve`/`.execute`. All 19 commission keys added to `SIGNED_IN_PERMISSIONS` demo seed (full set so both approve+execute paths are demoable; each action still gates on its own key). No missing keys.

## Mock contracts + adapter
`lib/mock/commission.ts` (re-exported from index): `listBrokers/registerBroker/setBrokerStatus/blacklistBroker`, `listCommissionRules/createCommissionRule`, `listAttributions`, `listPayouts/approvePayout/executePayout`, `listDisputes/raiseDispute/resolveDispute`. Schemas in contracts.ts mirror the DTOs (Broker has `maskedPhone`+`carePartnerLabel`, no PAN; CommissionRule `excludesPndt: literal(true)`; Payout full tax breakdown). Payout math helper: TDS 5%, GST 18% if registered, net = gross−tds+gst. All clearly-synthetic seed.

## Deferred / flags
- **Broker self-service portal** (read_self/update_self/generate_link_self + wallet) is a SEPARATE later deliverable — NOT built. Noted per brief.
- **No CommissionController yet** — all list endpoints built to spec (DTOs are mostly request/result); reconciliation pass to follow.
- Invoice/Form 16A download + ABDM-style file actions are stub buttons pending endpoints.
- **Menu seeding TODO** (slice-08): no nav row for `/care-partners` — mocked it; backend needs the navigation_menus row + `commission.broker.read` map.

## Build
typecheck + build green, no warnings. CarePartnersScreen ~27kB own chunk; panels split. New `handshake` icon in icons.tsx. New `commission.*` i18n namespace (en+hi, compiler-enforced parity).
