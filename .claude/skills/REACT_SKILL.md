# DocSlot UI/UX — React 19.2 Frontend Skill

> **Supersedes the Angular 21 SKILL.md** (archived in `archive/`) per ADR-015. The design DNA — palette, motion, interaction patterns — is unchanged and canonical (ADR-008). Only the implementation framework changed: **React 19.2 (pin react@19.2.7, react-dom@19.2.7)**.

## Stack (locked)

| Concern | Choice | Why |
|---|---|---|
| Framework | React 19.2 SPA + Vite 6 | Cloudflare Pages static deploy (zero-Dockerfile policy, ADR-009) |
| Routing | TanStack Router | Type-safe routes, search-param state |
| Server state | TanStack Query v5 | Cache, retries, optimistic mutations against DocSlot.API |
| Client state | Zustand | Slide-over stack, command palette, session |
| Forms | react-hook-form + zod | Schema-shared validation with API DTOs |
| Styling | Tailwind CSS v4 + CSS variables | Design tokens below |
| Components | Radix primitives + cmdk + sonner | Accessible slide-overs, palette, toasts |
| Compiler | React Compiler enabled | Auto-memoization; do NOT hand-write useMemo/useCallback unless profiled |
| i18n | react-i18next (en, hi) | Bilingual parity with WhatsApp + menu_label_hi |

React 19 features we USE: `useOptimistic` (booking status, slot selection), `useActionState` (form submits), `use()` for promise/context reads, Actions for mutations. We do NOT use Server Components — this is a Vite SPA against the .NET API.

## Design DNA (canonical — ADR-008, unchanged from prototype)

```css
:root {
  --cream:      #F6F4EE;   /* app background */
  --teal:       #1F5E50;   /* primary actions, active nav */
  --ink:        #0E1F1C;   /* text */
  --terracotta: #E0633A;   /* destructive, urgent badges */
  --motion:     cubic-bezier(0.32, 0.72, 0, 1);
  --dur-fast:   200ms;  --dur-base: 240ms;
}
```
- Near-flat shadows (`shadow-sm` max); depth via borders + background shifts, not elevation
- Right-side **slide-over panel is the PRIMARY CRUD modality** — not centered modals, not page navigations
- 8px spacing grid; 12px radius on cards, 8px on inputs/buttons

## The 15 canonical patterns → React implementations

1. **Slide-over drawer** (primary CRUD): Radix Dialog with `side="right"`, 420px desktop / full-width mobile, focus-trapped, closes on Esc + overlay, URL-addressable via TanStack Router search param (`?panel=booking-edit&id=...`) so refresh restores it.
2. **WhatsApp-mirrored chat view**: booking conversation timeline styled like WhatsApp (right-aligned tenant messages) — patients live in WhatsApp; staff must see the same thread.
3. **Command palette**: cmdk, `Cmd/Ctrl+K`, actions + patients + bookings search, permission-filtered from the effective permission set.
4. **Optimistic UI**: `useOptimistic` for booking confirm/cancel and slot holds; reconcile on mutation settle; toast+undo on success.
5. **Skeleton loaders**: shaped like real content (calendar grid skeleton, list-row skeleton); never spinners for primary content.
6. **Empty states**: illustration + one-line + primary action ("No bookings today — share your booking link").
7. **Toast + undo**: sonner; destructive actions get 5s undo window before the mutation fires (deferred mutation pattern).
8. **Inline editing**: click-to-edit on detail fields, Enter saves, Esc reverts, optimistic.
9. **Progressive disclosure**: advanced filters/sections collapsed by default; counts hint at content.
10. **Hover preview**: patient/booking hover-cards (Radix HoverCard) with key facts, 300ms open delay.
11. **Keyboard shortcuts**: `?` opens cheatsheet; j/k list nav; shortcuts registered per-route.
12. **Status pills with icons**: every status gets icon + color (confirmed=teal check, pending=amber clock, cancelled=terracotta x) — never color alone (a11y).
13. **Loading hierarchy**: route-level skeleton → section skeleton → inline spinner only for sub-actions.
14. **Focus management**: slide-over open → focus first field; close → return focus to trigger; route change → focus h1.
15. **Reduced motion**: all transitions behind `prefers-reduced-motion` guard.

## Backend-driven navigation (MANDATORY — RBAC_NAVIGATION.md)

```tsx
// On login: GET /api/v1/me/menus  → backend runs platform.get_user_menus()
const { data: menus } = useQuery({ queryKey: ['me','menus'], queryFn: fetchMenus });
// Render the returned tree. NEVER: {role === 'admin' && <NavItem/>}
```
- Menu tree comes from the server (tenant_type-aware, bilingual `menu_label` / `menu_label_hi`)
- Component-level permission checks read the effective permission set fetched once per session (`/api/v1/me/permissions`, backed by `resolve_user_permissions()`), held in Zustand, checked in memory
- Badge counts from `badge_source` keys → one batched `/api/v1/me/badges` poll

## Project structure

```
src/
  app/            router, providers (Query, i18n, theme)
  features/       bookings/ patients/ doctors/ commission/ settings/
    <feature>/    api.ts (queries+mutations) components/ routes.tsx
  components/ui/  design-system primitives (slide-over, pill, skeleton)
  lib/            api-client (fetch + JWT + tenant header), permissions.ts
  stores/         session.ts, ui.ts (zustand)
```
Feature-folder rule: a feature owns its queries, components, routes. Cross-feature imports only via `components/ui` or `lib`.

## Non-negotiables (pre-delivery checklist)

- [ ] All colors/motion from tokens — zero hex literals in components
- [ ] Slide-over (not modal/page) for create/edit
- [ ] Every list: skeleton + empty + error states implemented
- [ ] No hardcoded menu or role logic anywhere
- [ ] Hindi strings present for every user-facing label
- [ ] Keyboard + screen-reader pass on new screens (Radix gives most of it — don't break it)
- [ ] React Compiler on; no manual memo without a profiler trace
- [ ] Mutations idempotent-safe (Idempotency-Key header on POST)

## Healthcare-specific anti-patterns (carried from Angular skill)

Never: red for non-destructive medical statuses; dense tables for patient-facing views; auto-refresh that steals focus mid-form; timezone-naive slot display (always Asia/Kolkata explicit); medical jargon in patient-visible strings without Hindi equivalent.
