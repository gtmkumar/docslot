---
name: design-token-guard
description: The lint:tokens drift guard that enforces docs/font-color.md — how it works, what it flags, and its allowlist.
metadata:
  type: project
---

`frontend/scripts/check-design-tokens.mjs` (run via `npm run lint:tokens`) is the zero-drift guard proving the app follows the canonical token reference `docs/font-color.md`. Tokens themselves are defined ONCE in `frontend/src/styles/global.css` (`:root` + `[data-theme=dark]` + `@theme inline`), ported verbatim from font-color.md.

**Why:** font-color.md is the canonical font/colour source; components must reference CSS variables/Tailwind tokens, never raw hex/font stacks, so the runtime theme switcher and dark mode keep working.

**How to apply:** Dependency-free ESM (fs/path only). Walks `src/**/*.{ts,tsx}`, strips comments (respecting string literals so `//` in URLs and `#88a` issue-refs in comments don't trip it), then flags in non-comment code: (1) hex literal in a *styling context* (a CSS/style cue must precede it — so `href="#abc123"`, `#{token}`, `#anchors` are ignored), (2) Tailwind arbitrary colour class `(text|bg|border|fill|stroke|ring|from|to|via)-[#...]`, (3) `fontFamily`/`font-family` not referencing `var(--font`. Exit 1 + `file:line` on any hit.

**Allowlist (exempt, with reasons):** `src/lib/data.ts` (categorical demo dept/doctor swatches = data), `src/stores/ui.ts` (`<input type=color>` theme-picker default accent/primary need literal hex), `src/lib/mock/**` (seed data + mock notifier HTML email bodies), and any `*.html` (email templates — clients can't load web fonts). Do NOT add component files to the allowlist; fix them instead.

Wired in `frontend/package.json` scripts as `lint:tokens`. No `lint`/`pretest`/`test` script chain existed to append to. Passes clean on current tree (191 files, incl. the redesigned Bookings screen). Related: [[foundation-patterns]].
