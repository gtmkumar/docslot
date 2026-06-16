---
name: app-docslot-frontend
description: How to run and navigate the DocSlot reception-desk SPA under test (ports, routes, slide-over URL scheme, mock data)
metadata:
  type: reference
---

DocSlot frontend is a mock-first Vite SPA in `frontend/` (no backend needed).

Run: `cd frontend && npm run dev`. Port quirk: 5173/5174 are often taken by unrelated projects (laundryghar, snapaccount). It auto-bumps — confirm the actual port from the Vite log and that `<title>` is "DocSlot — Reception Desk". Stop with `pkill -f "docslot/frontend/node_modules/.bin/vite"`.

Mock latency is 180ms (`src/lib/mock/index.ts` `LATENCY`) so skeletons flash briefly — CPU-throttle (CDP `Emulation.setCPUThrottlingRate` rate 6) to catch them; heavy network throttle hangs Playwright screenshots on font load.

Routes (TanStack code-based, `src/app/router.tsx`): `/` = OverviewScreen; `/bookings /calendar /doctors /patients /analytics /team /settings` are placeholder screens ("Nothing here yet — arrives in a later wave").

Slide-overs are URL-addressable via root search params `?panel=<type>&id=<bookingId>`. Panel types: `conversation, manage, approve, newBooking, bookTime, addDoctor, addPatient`. manage/conversation/approve need a valid booking `id` (e.g. `B-2841`); the others are payloadless. Open from UI: queue "Manage appointment" button → manage; "+ New walk-in" → newBooking; "Book time" → bookTime; command-palette actions → addDoctor/addPatient.

Org switcher is a native `<select id="org-switcher">` (3 orgs: apollo-andheri=hospital, dr-mehta=individual_doctor, thyrocare-kandivli=pathology_lab) — drive with Playwright `selectOption`, NOT click (native popup won't open under automation). Changing it updates the breadcrumb context.

PHI rule: phones are MASKED in at-a-glance views (command palette shows `+91 ····· ···72`) and REVEALED full inside opened detail panels (Manage shows `+91 98203 14572`) — both are correct per design.
