---
name: docslot-qa-harness
description: Playwright QA harness location and how to run live screenshot/overflow tests
metadata:
  type: reference
---

A Playwright QA harness lives in `frontend/qa/` (added 2026-06). Playwright + chromium are installed as devDeps.

Run against a built preview (`npm run build && npm run preview` serves :4173):
- `BASE=http://localhost:4173 node qa/shoot.mjs <tag>` — logs in (demo creds) and screenshots all 11 routes × 3 viewports (mobile 390 / tablet 768 / desktop 1440) into `qa/shots/<tag>/`, reports console/page errors.
- `BASE=http://localhost:4173 node qa/overflow.mjs` — flags any route×viewport with horizontal document overflow (the precise responsive-defect detector).
- `node qa/panels.mjs` — clicks real triggers, asserts slide-overs open, screenshots them.

Two systemic responsive bugs were found & fixed this way: (1) `Button` (`components/ui/Button.tsx`) concatenates `className` after its base `inline-flex`, so a `hidden` passed via className is silently overridden — control display on a wrapper instead; (2) Tailwind `sr-only` (position:absolute) escapes an `overflow-x-auto` scroller unless the scroller is `relative`. Also: flex/grid children need `min-w-0` to shrink below content min-content. See [[repo-state-vs-claudemd]].
