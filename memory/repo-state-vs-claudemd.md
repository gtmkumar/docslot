---
name: repo-state-vs-claudemd
description: CLAUDE.md is stale — the DocSlot repo is far more built than it claims
metadata:
  type: project
---

CLAUDE.md says the .NET backend is "an early skeleton — only two shared library projects exist" and that the React frontend "is not yet in this tree." Both are **wrong as of 2026-06**. Verified on disk:

- **Backend** `backend/mediq/` has all 11 projects building green (exit 0): Domain, Application (custom CQRS, no MediatR), Infrastructure (EF database-first, tenant-scoped RLS), Api (13 controllers), Gateway (YARP), AppHost (Aspire), ServiceDefaults, Utilities, SharedDataModel, IntegrationTests. Architecture audit found it mature with **zero critical/high findings** (medium refactors: fat `IAttributionRepository` ISP, audit/event inlined in handlers should be pipeline behaviors, unused `Result<T>`).
- **Frontend** `frontend/` is a complete React 19.2 + Vite 6 SPA on **mock data** (`src/lib/mock/`), builds green. Demo login: `priyanka@apollocare.in` / `reception`.

When reasoning about this repo, **verify on disk** rather than trusting CLAUDE.md's maturity claims. See [[docslot-qa-harness]].
