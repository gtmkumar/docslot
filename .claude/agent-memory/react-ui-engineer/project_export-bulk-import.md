---
name: export-bulk-import
description: Slice #95 frontend — People-tab Export (members CSV download) + Bulk-import slide-over (client-side CSV parse → per-row provision) in the Team console.
metadata:
  type: project
---

# Team console — Export + Bulk import (#95, epic #80 Phase D)

Made the two #81 disabled "Coming soon" toolbar stubs LIVE in `TeamScreen.tsx`. No new tables; both hang off the existing tenant/user surface. See `.agents/memory/api-contracts.md` for the full contract dump.

**Why:** epic #80 unified Team console; #95 was the last toolbar action left stubbed.

**How to apply / key facts:**
- **Export = an authed blob fetch, NOT a link.** `GET /tenants/{id}/users/export` streams `text/csv` and needs `Authorization`+`X-Tenant-Id`, so `real.exportTenantUsers` does a raw `fetch` + `res.text()` + Content-Disposition filename, returns `{fileName,content}`. Reuse the new shared util `src/lib/download.ts` `downloadTextFile()` (also now used by `AuditLogTab`, whose private `downloadCsv` was refactored to delegate). Same posture as the #86 audit export — never cached.
- **Bulk import** = `POST /tenants/{id}/users/bulk-import` body `{rows:[{email,fullName,roleKey?}]}` (Idempotency-Key), per-row atomic, R3 no-escalation on role, batch cap 500 → 422. Result summary counts are **server-authoritative** (render directly, don't recompute from rows). `BulkImportResultRow.status` kept as a **plain string** (not a zod enum) so an additive backend status still parses — UI lower-cases for the pill lookup + falls back to neutral/raw label.
- **The panel** `BulkImportUsersPanel.tsx` parses CSV CLIENT-SIDE (RFC-4180-ish `parseCsv`: quoted fields, `""` escapes, embedded commas/CR/LF, BOM strip), with a header toggle + auto-detected/overridable column mapping, an 8-row preview with per-row validation, the 500-cap guard, then a result phase (summary chips + per-row pills). Role column maps to role KEYS passed straight through — the server resolves + enforces, so no client role list.
- **New slide-over panel type `bulkImportUsers`** must be added in THREE places or tsc/nav breaks: `stores/ui.ts` Panel union, `SlideOverHost` (lazy + PAYLOADLESS + render case), and **`app/router.tsx` `panelSearchSchema` z.enum** (the `?panel=` source of truth — easy to forget, it's a hard tsc error). Payloadless + URL-addressable (parsed rows are panel-local, never URL-encoded).
- **Gating:** Export → `tenant.users.read`; Bulk import → `tenant.users.create` (in-memory `can()`, no role branches). Mock `SIGNED_IN_PERMISSIONS` already had both.
- **Mock (`rbac.ts`)** builds the export CSV from + PUSHES bulk-created rows INTO the `USERS` seed, so the People-list refresh (`onSuccess` invalidates `['team','users']`) actually shows new members flag-off.

Related: [[project_team-audit-sessions]] (the audit CSV export this mirrors), [[project_iam-matrix]] (the People/Roles console), [[project_invitations]], [[project_branch-scope]].
