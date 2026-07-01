---
name: team-audit-sessions
description: Team console Audit log (#86) + Active sessions (#87) frontend — endpoints under /security surfaced in /team; CSV auth-fetch, useOptimistic revoke, lastActivityAt Online dot.
metadata:
  type: project
---

Slice #86/#87 (epic #80 Phase B) wired the Team console **Audit log** + **Active sessions** tabs, replacing their empty-state stubs. See also [[project_iam-matrix]] (the 6-tab console shell) and [[project_contract-surface]] (the seam).

**Where things live**
- Endpoints are under `/api/v1/security/*` but the UI lives in the **Team console** (`/team`), not the separate `/security` screen. Gates: Audit → `tenant.audit.read`, Security/sessions → `tenant.users.update` (in-memory `can()`, tabs already gated in `TeamScreen.tsx`).
- Seam: `listAuditLog`, `exportAuditLog`, `listActiveSessions`, `revokeSession`, `revokeAllSessions` in `lib/backend/index.ts`. Real in `lib/backend/real.ts`; mock in NEW `lib/mock/team-audit-sessions.ts`. Hooks appended to `features/team/api.ts`. Components: `features/team/components/{AuditLogTab,SessionsTab}.tsx`.

**Non-obvious decisions worth remembering**
- **CSV export must fetch WITH auth headers.** The `/security/audit/logs/export` endpoint streams text/csv + Content-Disposition and needs Bearer + X-Tenant-Id, so a bare link/window.open won't authenticate. `real.exportAuditLog` mirrors `openForm16ADocument`: auth-fetch → `res.text()` → parse filename from Content-Disposition → return `{fileName,content}`; the COMPONENT blob-downloads and discards (never in a query key). Hence `useExportAuditLog` is a mutation, and `AuditCsvResult`/`AuditLogFilter` are plain TS interfaces (not zod wire schemas).
- **Facets are range+search scoped, independent of the selected category/severity** — so counts don't collapse when you pick a facet. Mock computes them this way; keep it if the backend semantics are ever questioned.
- **Sessions revoke = `useOptimistic` + surgical `setQueryData` on success (NOT invalidate).** Invalidating would refetch and cause the revoked row to flash back before the server list updates. Surgical removal keeps the optimistic overlay and base state in agreement. Optimistic reverts on error.
- **`UserListItemDto.LastActivityAt` (nullable)** is the "Online" signal for the People tab: `isActive && now-lastActivityAt < 5min` → avatar presence dot + "Online now"; else "last seen {relative}". Added `.default(null)`/`.optional()` to both `UserListItemSchema` and `UserListItemDtoSchema` so older/omitting responses still parse.

Routes added: none (both are in-tab surfaces, not slide-overs). i18n `team.audit.*` / `team.sessions.*` / `team.{onlineNow,lastSeen}` (en+hi, `count_one/_other` plurals). typecheck + build green.
