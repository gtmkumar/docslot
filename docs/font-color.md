# DocSlot Dashboard — Font Color Reference

All text colors are CSS custom properties defined in `DocSlot Dashboard.html` (`:root` for light mode, `[data-theme="dark"]` for dark mode). Never hardcode hex values in components — always reference the variable so the Tweaks panel's theme switcher keeps working.

## Core text colors

| Variable    | Light mode                          | Dark mode | Used for                                                                                              |
| ----------- | ----------------------------------- | --------- | ----------------------------------------------------------------------------------------------------- |
| `--ink`     | `#0E1F1C` (deep forest, near-black) | `#ECEFEC` | Primary body text, headings, active nav items, table cell values                                      |
| `--ink-2`   | `#1E332E`                           | `#C9D2CC` | Secondary emphasis text — patient names in bold rows, doctor names, slightly-softer-than-ink headings |
| `--muted`   | `#5C6F69`                           | `#94A39D` | Labels, eyebrows (uppercase kickers), timestamps, helper text, inactive nav items, table headers      |
| `--muted-2` | `#8A9994`                           | `#6F7E78` | Lowest-emphasis text — disabled states, placeholder-like hints, scrollbar thumb                       |

## Brand colors (used as text color, not just backgrounds)

| Variable         | Light mode                  | Dark mode | Used for                                                                                                                                |
| ---------------- | --------------------------- | --------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `--primary`      | `#1F5E50` (deep teal-green) | `#4FB39A` | "Confirmed" status text, primary CTA emphasis, Manage Appointment section headers ("Appointment Details"), positive deltas in Analytics |
| `--accent`       | `#E0633A` (warm terracotta) | `#ED7B53` | Today's date highlight in Calendar, accent buttons, Tweaks accent-color swatch, "today" column header                                   |
| `--whatsapp`     | `#25D366`                   | _(same)_  | WhatsApp icon glyphs specifically (not the label text)                                                                                  |
| `--whatsapp-ink` | `#075E54`                   | _(same)_  | "WhatsApp" source-pill label text, chat bubble sender ticks                                                                             |

## Status / semantic text colors

These pair a solid color (for text/icons) with a `-soft` background variant (for pill fills). Used across `StatusPill`, booking rows, Team & Roles badges, and the Admin console.

| Status             | Variable   | Light mode | Dark mode     | Used for                                                                               |
| ------------------ | ---------- | ---------- | ------------- | -------------------------------------------------------------------------------------- |
| Warning / Pending  | `--warn`   | `#C2871B`  | _(unchanged)_ | "Pending" status text, no-show warnings, 2FA-off indicator, past-due billing text      |
| Danger / Cancelled | `--danger` | `#B5392E`  | _(unchanged)_ | "Cancelled"/"No-show" status text, destructive button labels (Reject, Delete, Suspend) |
| Info               | `--info`   | `#2A5C8A`  | _(unchanged)_ | Informational badges (rarely used directly as text; mostly reserved for future use)    |

> Note: `-soft` suffixed variables (`--warn-soft`, `--danger-soft`, `--primary-soft`, `--accent-soft`, `--whatsapp-soft`, `--info-soft`) are **background fills only** — pair them with their solid counterpart for text color to keep contrast correct in both themes.

## Surface-based text (inverted contexts)

| Variable | Light mode                 | Dark mode | Used for                                                                                                             |
| -------- | -------------------------- | --------- | -------------------------------------------------------------------------------------------------------------------- |
| `--bg-2` | `#FBFAF5` (warm off-white) | `#0F1A17` | Text color _on top of_ dark surfaces — e.g. white-ish text on the black "Live queue" card, primary button label text |

## Font families (paired with the above colors)

| Variable      | Stack                                                 | Used for                                                                                                                                                  |
| ------------- | ----------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--font-sans` | `"Inter Tight", ui-sans-serif, system-ui, sans-serif` | All UI text by default                                                                                                                                    |
| `--font-mono` | `"IBM Plex Mono", ui-monospace, monospace`            | Tokens (`#12`), phone numbers, currency (`₹900`), timestamps, IDs — anything tabular/numeric. Apply via `.mono` class or `fontFamily: "var(--font-mono)"` |
| `--font-deva` | `"Noto Sans Devanagari", "Inter Tight", sans-serif`   | Hindi patient notes and greetings (e.g. "भूख नहीं लग रही") — apply via `.deva` class whenever `lang === "hi"`                                             |

## Where to look in code

- Token definitions: `DocSlot Dashboard.html` → `<style>` → `:root` and `[data-theme="dark"]`
- Reusable text-color logic: `src/ui.jsx` (`StatusPill`, `SourcePill`, `Btn` variants, `SectionTitle`, `KBD`)
- Per-screen usage: `src/screens/*.jsx` — search for `color: "var(--` to find every instance

## Adding a new color

1. Add the light-mode value under `:root` and the dark-mode override under `[data-theme="dark"]`.
2. If it's a status color, also add a `-soft` background variant.
3. Reference it as `var(--your-name)` in JSX — never inline a hex code.
