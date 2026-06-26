---
name: known-defects-roles
description: Known defects on Team & Roles screen as of 2026-06-26 — Users-tab Zod crash, custom roles absent from list
metadata:
  type: project
---

Found 2026-06-26 during live E2E of Team & Roles → Roles & permissions (admin@docslot.io).

**D1 (blocker, FRONTEND): Users tab renders "Something went wrong" on a valid 200.**
`GET /api/v1/tenants/{id}/users` returns 200 with `[{userId,email,fullName,phone:null,isActive,mfaEnabled,lastLoginAt}]`. But `listTenantUsers()` in `src/lib/backend/real.ts` runs `UserListItemSchema.array().parse(raw)`, and `UserListItemSchema` (src/lib/mock/contracts.ts) REQUIRES `maskedPhone` (z.string().nullable()) and `roles` (z.array). API sends `phone` not `maskedPhone`, and omits `roles` → Zod throws → React Query isError → error state. No console error is logged (RQ swallows it). Blocks Users tab entirely, and thus effective-access + per-user overrides (both reached via the user row). Fix: align DTO (BE adds maskedPhone+roles[]) OR make schema tolerant (FE: phone→maskedPhone, roles default []).
**Why:** contract drift between live API and the FE Zod contract. **How to apply:** when QA'ing any list screen, capture the app's authenticated XHR body and diff against the feature's `*Schema` — Zod `.parse()` failures surface as generic error states, not console errors.

**D2 (major, BACKEND): custom/duplicated roles never appear in the roles list.**
Duplicate succeeds (POST /iam/roles/duplicate → navigates to new custom role's editable matrix at `?panel=roleMatrix&id=<newId>`), but `GET /api/v1/roles` returns ONLY the 9 system roles (`isSystem:true`, `tenantId:null`) every time — the new custom role is absent. So custom roles can't be reopened from the list after creation. FE renders the payload faithfully. Owner: backend (`/roles` must include tenant/custom roles).

**What PASSED (works end-to-end):** role list render + System badges; built-in matrix read-only (33/33 cells disabled, read-only notice, granted/total tallies, danger dots, Duplicate CTA, right-anchored slide-over); duplicate flow (lower_snake key, navigates to new editable matrix); custom matrix optimistic toggle (aria-checked true→false→true, no error toast); dangerous-cell inline confirm step (does not apply until Confirm); bilingual hi labels present for full team.matrix.* namespace; Esc closes panel; differential gating (priyanka sees Duplicate but not Create role). Zero console/page errors, zero network 4xx/5xx on the app's own requests.
