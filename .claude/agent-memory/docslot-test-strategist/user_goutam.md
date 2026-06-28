---
name: user-goutam
description: User profile — Goutam Kumar, owns DocSlot QA/test strategy, DB-first, real-PostgreSQL validation non-negotiable
metadata:
  type: user
---

Goutam Kumar (mr.gtmkumar@gmail.com) is building DocSlot and drives its testing strategy.

- Treats the PostgreSQL schema in `database/` as the authoritative artifact; app is built FROM the schema.
- Wants integration tests run against the LIVE DB, with tests seeding + cleaning their own rows.
- Strong rule: when a NEW test reveals a real backend bug, REPORT it clearly rather than weakening the
  assertion to make it pass ("never weaken an assertion to make it pass"). The backend is assumed correct; a
  failing new test is either a real bug or a test-harness defect (e.g. cleanup FK trap), not license to relax.
- Phase-1 booking work is split across ledger tasks #27–#34; #34 is the integration-test + auditor gate.
