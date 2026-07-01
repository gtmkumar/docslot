---
name: invitations
description: Slice #89 frontend — token-based invitations in the Team console Invites tab; one-time-token reveal UX, surgical-cache actions, and the list-carries-no-inviter-name contract quirk.
metadata:
  type: project
---

Issue #89 (epic #80 Phase C) wired token-based tenant onboarding into the `/team` Invites tab, replacing the Phase-A "coming soon" empty-state. This sits ALONGSIDE the direct-add invite (`inviteUser` / `createUser`), it does NOT replace it.

**Endpoints + contract (mirrors `mediq.SharedDataModel/Docslot/Admin/InvitationDtos.cs`, camelCase wire):**
- `GET /tenants/{tenantId}/invitations?status=pending` → `InvitationList {items,count}`; `POST .../invitations` → `InvitationTokenResult`; `POST .../invitations/{id}/resend` → `InvitationTokenResult`; `POST .../invitations/{id}/revoke` → `RevokeInvitationResult {invitationId,alreadyInactive}`. tenantId from `getSessionSnapshot().tenantId`.
- **The frontend does NOT wire `POST /invitations/accept`** — that is the unauthenticated invitee redemption flow (the token IS the authorization), out of scope for the admin console. Redeem/delivery UI is **#93**.
- zod in `lib/mock/contracts.ts`: `InvitationSchema` (status `z.enum(['pending','accepted','revoked','expired'])`), `InvitationListSchema`, `InvitationTokenResultSchema`, `RevokeInvitationResultSchema`, `CreateInvitationRequestSchema {email, roleId?}`. `token` is ONE-TIME plaintext.

**Permission keys consumed (in-memory `usePermissions().can()`):** list + tab badge → `tenant.users.read`; create/resend/revoke → `tenant.users.create`.

**Contract quirk (worth remembering):** the list DTO carries `invitedByUserId` (Guid) but NO inviter display name — only `roleName` is JOINed. The console resolves "invited by" client-side from the cached People directory (`useTenantUsers` → `Map<userId,fullName>`); unresolved ids just omit the clause. If ever needed server-side, request `InvitedByName`.

**One-time-token UX pattern (reused precedent = developer-portal `clientSecret`):** create AND resend both return a fresh `token`. Both converge on a TRANSIENT reveal slide-over `invitationToken` (store payload `{result,email}`, copy button + a #93 hand-off note). `NewInvitationPanel` (email + optional role picker) opens it on success. The reveal is DELIBERATELY excluded from the router `panelSearchSchema` and `SlideOverHost` TRANSIENT_SET so the token can't survive a refresh (re-mint via resend). The `newInvitation` form panel IS URL-addressable/payloadless.

**Actions pattern:** `useResendInvitation`/`useRevokeInvitation` do surgical `setQueryData` on `['team','invitations','pending']` (resend patches resendCount+expiresAt, revoke drops row + decrements count) — NOT invalidate — so there's no refetch flash and the tab's `useOptimistic` overlay agrees with base on settle. The tab badge in `TeamScreen` reads the SAME cache key, so a revoke decrement updates it live. Same discipline as [[team-audit-sessions]] session revokes.

**Files:** `lib/mock/{contracts,invitations,index}.ts`, `lib/backend/{real,index}.ts`, `features/team/api.ts`, `features/team/components/{InvitesTab,NewInvitationPanel,InvitationTokenPanel}.tsx`, `features/team/TeamScreen.tsx`, `stores/ui.ts` (panels `newInvitation`/`invitationToken`), `components/layout/SlideOverHost.tsx`, `app/router.tsx` (enum), `app/i18n.ts` (`team.invites.*`). This is the Invites piece of the [[iam-matrix]] 6-tab console. `npm run typecheck && npm run build` green.
