---
name: screen-conventions
description: How DocSlot admin list/grid screens are built — states, filters, slide-over CRUD, in-memory perms, tokens
metadata:
  type: project
---

Established conventions for building a new feature screen in `frontend/src/features/<name>/`. Derived from the existing screens (Patients, Overview, Developers, CarePartners) and confirmed by the bookings/calendar/doctors/analytics wave.

**How to apply:** follow these exactly when adding any new screen so it matches the codebase.

- Screen root: `<section aria-labelledby="screen-heading">` with `<h1 id="screen-heading" tabIndex={-1} className="text-2xl font-semibold tracking-tight text-ink outline-none">`. Route-change focus targets this h1 (pattern 14).
- Three states ALWAYS, in this order: `isError` → Card+`EmptyState` with retry calling `refetch()`; `isLoading || !data` → a content-shaped Skeleton (not a spinner); empty → `EmptyState` (distinguish truly-empty vs filtered-empty with different i18n keys + a primary action only when truly empty).
- CRUD is via the Zustand slide-over store: `useUI((s) => s.openPanel)` then `openPanel({ type: 'newBooking' })` etc. `SlideOverHost` syncs store↔URL (`?panel=&id=`), so screens NEVER navigate or touch the URL for panels. Panel types live in `stores/ui.ts` Panel union AND the router search enum in `app/router.tsx` — both already populated for booking/doctor/calendar panels.
- Permissions: `const { can } = usePermissions()`; gate buttons/actions with `can('docslot.booking.create')` etc. Fail-closed. NEVER a `role === ...` branch. Keys consumed by this wave: `docslot.booking.read/create/approve`, `docslot.patient.update` (used for add-doctor — there is NO `docslot.doctor.create` in the seed), `docslot.slot.read` (implied for calendar), `docslot.analytics.read`.
- Tabs/filters: Radix `Tabs.Root` for status tabs (see DevelopersScreen `tabTrigger` class string), or plain `role="tab"` pill buttons for department filters. Filter counts come from the already-loaded in-memory list, never a per-tab fetch.
- Tokens only — ZERO hex in components. Specialty/department tints use a `colorKey`→token-class map (e.g. `bg-primary-soft text-primary`), mirroring `ProgressBar`'s `colorKey` and `DEPT_COLOR_KEY` in the mock adapter. Charts use inline SVG/CSS with token bg classes + percentage widths (no chart library — package.json has none and we don't add one).
- Slot/time display always `istSlot(time)` → "HH:MM IST". Phone always masked in lists (`maskPhone`, already applied in the adapter). Money via `inr()`.
- Every user-facing string is `t('ns.key')` with en+hi added to `app/i18n.ts` (`hi` is typed `typeof en`, so a missing Hindi key is a compile error). Devanagari free-text (patient notes) gets the `.deva` class when it matches `/[ऀ-ॿ]/`.
- React Compiler is ON — no `useMemo`/`useCallback`/`memo` without a profiler trace.
- Build check: `npm run build` (runs `tsc -b && vite build`). Each screen should code-split into its own chunk via `lazy()` in `app/router.tsx`.
