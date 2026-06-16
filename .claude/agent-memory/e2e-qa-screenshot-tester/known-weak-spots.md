---
name: known-weak-spots
description: Recurring DocSlot frontend defect areas to re-verify on every QA pass
metadata:
  type: project
---

Re-check these every run. D1 + D2 were FIXED and re-verified 2026-06-14 (kept here as regression guards).

1. **[FIXED 2026-06-14] Live Queue emphasis card contrast (was BLOCKER).** Fix: `Card.tsx` now takes `tone="surface"|"emphasis"`; `emphasis` = `border-ink-2 bg-ink text-bg` owned by the Card itself (no appended `bg-ink` className → no Tailwind v4 source-order conflict). `StatCards.tsx` uses `<Card tone="emphasis">`. Re-verified contrast 15.51 (light) / 16.14 (dark), both >> 4.5. **Regression guard:** computed bg of LIVE QUEUE card must be dark `rgb(14,31,28)` in light theme (not white); number readable in both themes.

2. **[FIXED 2026-06-14] Slide-overs survive refresh (was HIGH).** Fix: `SlideOverHost.tsx` store→URL writer skips its first mount run (`writerFirstRun` ref) so it can't erase the deep-linked param before URL→store hydration; `lastSyncedType` dedupes both directions. Re-verified: `?panel=manage&id=B-2841` + hard reload restores the panel; clicked-Manage + reload also survives, URL keeps `?panel=&id=`. **Regression guard:** goto deep-link → `location.reload()` → dialog still visible.

3. **[likely FIXED — verify] theme/density toggle.** A theme-toggle icon now appears in the Topbar (next to "+ New walk-in") as of 2026-06-14 — earlier it was missing (D3). Confirm it actually flips `data-theme` on `<html>` and density is reachable before reopening.

4. **Mobile (≤390px) sidebar doesn't collapse (MEDIUM — STILL OPEN).** Full sidebar stays, squeezing content; topbar buttons + revenue card overflow off-screen; no hamburger/drawer. Not re-tested in the 2026-06-14 quick pass.

5. **a11y console noise:** Radix DialogContent missing DialogTitle/Description on some panels (command palette / shortcuts), and TanStack Router has no `notFoundComponent` (invalid routes render the overly-generic default). LOW each.

Things that are SOLID (don't re-litigate): command-palette phone masking, Manage/Conversation/Approve&Collect/walk-in-stepper panel fidelity to prototypes, approve optimistic+toast+5s-undo, j/k+Enter queue nav, ? cheatsheet, skeleton/empty states, Idempotency-Key wiring on POST seam, org-switcher context change.
