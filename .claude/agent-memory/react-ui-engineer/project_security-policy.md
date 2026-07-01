---
name: security-policy
description: Team console Security tab (#91) frontend — tenant security policy (2FA/password-session/access restrictions) + IP allow-list, stacked above the #87 sessions panel. Endpoints, gating split, passthrough+Omit gotcha, honesty labels.
metadata:
  type: project
---

# Team console — Security policy (#91, epic #80 Phase C, frontend, 2026-07-01)

Wired the .NET #91 tenant SECURITY-POLICY surface into the Team console **Security** tab. The backend already existed (`SecurityPolicyController` + `SecurityPolicyFeatures` + `SecurityPolicyDtos.cs`) — this was pure frontend wiring. See also [[project_team-audit-sessions]] (the #87 sessions panel this composes) and [[project_iam-matrix]] (the 6-tab console shell).

## Where it lives
- `features/team/components/SecurityTab.tsx` REPLACED `<SessionsTab/>` as the Security tab body in `TeamScreen.tsx`; it renders the 3 policy SectionCards then composes `<SessionsTab/>` UNCHANGED below. Keep that composition — the sessions panel is a separate slice.
- `features/team/components/IpAllowlistEditor.tsx` (the CIDR list+add+remove, its own perm plane).
- NEW shared primitive `components/ui/Toggle.tsx` — accessible `role="switch"` (no Radix switch dep in the repo; the shipped Radix set is only dialog/hover-card/tabs). Reuse it for any on/off toggle.

## Endpoints + gating (three permission planes — DO NOT conflate)
- Base `/api/v1/security` (tenant bound from JWT — NO `/tenants/{id}` prefix, unlike invitations/branches, so no `getSessionSnapshot()`).
- `GET /policy` → `SecurityPolicyDto` (gated `tenant.settings.read` — the tab already checks it).
- `PUT /policy` → re-read `SecurityPolicyDto` with fresh `staffPendingMfaEnrolment` (gated `tenant.settings.update`; ranges validated server-side → 422). Without settings.update the WHOLE form is read-only (inputs disabled, no save bar, a view-only banner).
- `GET/POST/DELETE /ip-allowlist[/{id}]` gated **`platform.ip_allowlist.manage`** — a SEPARATE plane from the `ipAllowlistEnabled` policy toggle (settings.update flips the toggle; ip_allowlist.manage edits the CIDR list). Without it the editor shows an honest "no access" note but the toggle still works. POST returns a **bare Guid string**; DELETE returns bool (404 cross-tenant, soft-deactivate).

## Contract shape + the passthrough/Omit GOTCHA
- `SecurityPolicyDto` is FLAT: mfaPolicy(`optional|owners_admins|all`), minPasswordLength, idleTimeoutMinutes, requireNewDeviceVerification, restrictLoginHours, loginHoursStart/End(HH:mm), doctorsExemptFromHours, ipAllowlistEnabled, maskSensitiveForReceptionist, + derived read-only staffPendingMfaEnrolment.
- **zod (contracts.ts):** split `SecurityPolicyFieldsSchema` (10 editable fields, its own object) and `SecurityPolicyViewSchema = Fields.extend({staffPendingMfaEnrolment}).passthrough()`. `SecurityPolicyInput = z.infer<Fields>`. **NEVER `Omit<View,'staffPendingMfaEnrolment'>`** — `.passthrough()` adds a `[k]:unknown` index signature and `Omit` collapses the entire type to `{[k]:unknown}` (every field becomes `unknown` → a wall of TS2322/TS18046). Cost me the first typecheck pass.
- `IpAllowlistEntrySchema` {allowlistId, cidrRange, label?, isActive, createdAt, expiresAt?} (dates ISO from .NET DateTimeOffset). `AddIpAllowlistRequest` is a plain TS interface.

## "N of M staff have 2FA" is CLIENT-derived (contract note)
GET /policy carries ONLY `staffPendingMfaEnrolment` (the pending-enrolment estimate), NOT with-2FA/total counts. So the mockup's "N of M have 2FA" is derived from the People list (`useTenantUsers` → active users' `mfaEnabled`), shown only when `tenant.users.read` is held. A settings-only viewer sees the pending warning (from the DTO) but not the coverage line. If that must change, request `staffWithMfa`/`staffTotal` on the DTO.

## Save model + hooks (features/team/api.ts)
- `useSecurityPolicy` key `['team','securityPolicy']`. `useUpdateSecurityPolicy` is OPTIMISTIC: onMutate spreads sent fields over the cache (KEEPS prior derived count — can't recompute pending client-side), onError rollback, onSuccess replaces with the server DTO (fresh count), onSettled invalidate. The form is a single local `draft` (dirty vs cache via JSON.stringify) with ONE sticky "Save changes"/"Discard" bar; the CIDR add/remove are immediate (separate endpoints), NOT part of the policy save. `useAddIpAllowlist` invalidates; `useRemoveIpAllowlist` surgical `setQueryData` drop + invalidate.

## Honesty labels (design DNA — no fake behavior)
- New-device toggle: label says e-mail DELIVERY of the code arrives later (#93 family) — tracking is stored now. idle-timeout hint: enforcement lands with the session layer (stored + range-validated now). Both are stored-but-not-fully-enforced per the backend deferred notes — surface honestly, never imply it works.
- **Auditor false-assurance sweep (2026-07-01, do-not-commit):** the auditor flagged three misleading strings; corrected in-place. (1) The receptionist mask governs the patient PHONE NUMBER only (email/DOB stay visible) — label is now `team.security.access.mask` = "Mask patient phone number for reception roles", subtext names phone + "Email and date of birth stay visible." Do NOT relabel it "sensitive data" again unless the backend actually masks email/DOB. (2) Idle timeout is NOT enforced server-side yet → a token-only "Not yet enforced" pill (`session.notEnforced`, `bg-surface-sunk text-muted-2`) sits next to the idle label. (3) 2FA "Required" tiers have NO TOTP enrolment flow yet → an enrol-pending note (`mfa.enrolNote`) renders under the radios whenever `draft.mfaPolicy !== 'optional'`. All three keys added en+hi (typecheck enforces parity).

## Mock note (why the tab now shows flag-off)
Added `tenant.settings.read`, `tenant.settings.update`, `platform.ip_allowlist.manage` to `lib/mock/index.ts` SIGNED_IN_PERMISSIONS — previously priyanka lacked settings.read so the Security tab (and #87 sessions) were HIDDEN in mock mode. NEW mock `lib/mock/security-policy.ts` seeds a configured Apollo Care policy (mfaPolicy=owners_admins → pending=1 in the demo) + 2 CIDRs; derives pending from `listTenantUsers()`.

Seam fns (lib/backend/{index,real}.ts): getSecurityPolicy/updateSecurityPolicy/listIpAllowlist/addIpAllowlist/removeIpAllowlist. i18n `team.security.*` (en+hi, `pending_one/_other` + `minutesOption`/`hoursOption` plurals). No new routes/URL panel params. typecheck + build green.
