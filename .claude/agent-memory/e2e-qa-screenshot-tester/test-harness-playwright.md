---
name: test-harness-playwright
description: Playwright is not installed in the repo; drive it by importing from the npx cache that matches the cached browser build
metadata:
  type: reference
---

Playwright is NOT in `frontend/node_modules`. It is available via the global npx cache. Import the ESM entry directly in a `.mjs` driver script:

`import { chromium } from '/Users/gtmkumar/.npm/_npx/<hash>/node_modules/playwright/index.mjs';`

Pick the cache hash whose `playwright-core` version matches an installed browser revision under `~/Library/Caches/ms-playwright/`. As of 2026-06: use `e41f203b7505f1fb` (playwright 1.60.0 → chromium rev 1223, which IS installed). Do NOT use `9833c18b2d85bc59` (1.61-alpha → expects rev 1226, NOT installed → "Executable doesn't exist").

Run drivers from `/tmp` with plain `node driver.mjs`. Screenshots go to `frontend/.qa-screenshots/` (not committed).

To inspect computed styles (e.g. proving a contrast bug), use `page.evaluate(() => getComputedStyle(el))` — far more reliable than eyeballing screenshots.
