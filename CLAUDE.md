# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**DocSlot** — a WhatsApp-first, multi-tenant healthcare SaaS for the Indian market (appointment booking, prescriptions, lab reports, ABDM/ABHA-integrated medical records, broker-referral commissions). Intended runtime is two services: a **.NET 10 transactional service** (system of record) and a **Python AI sibling service** (LangGraph triage, RAG, OCR, no-show prediction), with a **React 19.2 SPA** admin/staff frontend.

**Maturity matters when deciding where truth lives:** the PostgreSQL schema in `database/` is the mature, authoritative artifact (113 tables, verified end-to-end). The .NET backend (`backend/mediq/`) is an early skeleton — only two shared library projects exist so far. The Python service and React frontend are specified but not yet in this tree. Build the app *from* the schema, not the other way around.

## Repository layout

- `database/` — **canonical** PostgreSQL schema. Source of truth for the entire data model.
- `docs/` — `DATA_DICTIONARY.md` (all 113 tables + columns), `REQUIREMENTS.md`, `COMMISSION_SYSTEM.md`, `INDEX.md`, `screens/` (UI mockups), flow PNGs. Note: `INDEX.md` lists many docs (PRODUCTION_SPEC, ARCHITECTURE, etc.) that are not present in this tree — treat that list as aspirational, not a file manifest.
- `backend/mediq/` — .NET 10 solution. **This is its own nested git repo** (the project root is *not* a git repo). Currently: `mediq.Utilities` (cross-cutting: Result types, API response envelopes, exception middleware, email, encryption helpers) and `mediq.SharedDataModel`.
- `.claude/skills/` — authoritative build conventions: `REACT_SKILL.md` (frontend design DNA), `SECURITY.md` (threat model + DPDP), `dotnet10-microservices-architect/` (backend patterns + `references/`).
- `.claude/agents/` — 7 specialist subagents and a wave-build orchestrator. The orchestrator expects a `.claude/agents/orchestrator.md`, a master ledger, and an ownership map; if those are absent, the wave build cannot run.

## Database — the heart of the system

Nine numbered SQL files run in dependency order; `docslot_complete.sql` is the bundled equivalent (regenerated from the numbered files — **if you edit a source file, regenerate the bundle**).

```bash
# Fresh DB, everything at once (PostgreSQL 16+ with pgvector):
createdb docslot_platform
psql -d docslot_platform -f database/docslot_complete.sql

# Or individual files — required order is 01 → 02 → 03 → 05 → 06 → 07 → 08 → 09 → 04 → 10 → 11
psql -d docslot_platform -f database/01_platform_core.sql   # tenants, users, RBAC, audit, billing (18)
psql -d docslot_platform -f database/02_platform_api.sql    # OAuth/webhooks PaaS layer (8)
psql -d docslot_platform -f database/03_docslot.sql         # DocSlot product schema (26)
psql -d docslot_platform -f database/05_security_hardening.sql  # encryption, RLS, audit chain (13) — REQUIRED for real patient data
psql -d docslot_platform -f database/06_ai_services.sql     # LangGraph/RAG/OCR schema (10) — only if running the AI service
psql -d docslot_platform -f database/07_commission_broker.sql   # broker referral + commission (10)
psql -d docslot_platform -f database/08_rbac_navigation.sql # backend-driven menus + per-user overrides (5)
psql -d docslot_platform -f database/09_chat_identity.sql   # WhatsApp identity + direct-booking discount, broker mutual-exclusivity (1)
psql -d docslot_platform -f database/04_future_products.sql # RuralReach/SafeHer/GenericFirst (22, optional)
psql -d docslot_platform -f database/10_roles_grants.sql    # least-privilege docslot_app role (NOSUPERUSER/NOBYPASSRLS) + grants (0 tables, idempotent) — runs LAST
psql -d docslot_platform -f database/11_rbac_hardening.sql  # RBAC hardening R1–R6: RLS on RBAC tables, escalation guard, SoD, super_admin cross-tenant (2)
```

The bundle is **NOT idempotent** — designed for fresh databases; drop schemas/DB to re-run. See `database/README.md` for the full architecture narrative.

### Schema invariants that drive application code

- **RBAC, no hardcoded role checks anywhere.** Permissions → roles → `user_tenant_roles` → users. Check access via `platform.user_has_permission(user, key, tenant)`; the effective set comes from `platform.resolve_user_permissions()`. Per-user overrides are **deny-wins** and time-boxable.
- **Backend-driven navigation.** Menus come from `platform.get_user_menus()` (tenant-type-aware, bilingual). The frontend renders the returned tree — it never branches on role in JSX.
- **Multi-tenant isolation.** Every product row carries `tenant_id` (FK to `platform.tenants`); composite indexes lead with `tenant_id`. Sensitive tables enforce RLS via `current_setting('app.tenant_id')` — app code must `SET LOCAL app.tenant_id` per transaction. **Exception:** `docslot.patients` is intentionally cross-tenant (phone number is global identity); tenant linkage lives in `docslot.patient_tenant_links`.
- **Soft deletes everywhere** (`deleted_at`) — never physically DELETE; never DELETE from `audit_log`.
- **Compliance is enforced in the schema, not just code.** Field encryption is declared in `platform.encrypted_fields_registry`; the audit log is hash-chained (`platform.verify_audit_chain()`); DPDP rights, breach reporting, and cryptographic erasure (key destruction, not row deletion) are table-backed. PCPNDT/MCI rules live in CHECK constraints that code cannot bypass.
- **Human-readable IDs** via triggers: `BKG-/PRX-/RPT-YYYY-MM-NNNNN`. UUIDs remain the PKs.

## .NET backend

```bash
dotnet build backend/mediq/mediq.sln
dotnet test  backend/mediq/mediq.sln   # no test projects exist yet — add under backend/mediq/
```

Target framework is `net10.0` with nullable + implicit usings enabled; EF Core 10. Conventions are dictated by `.claude/skills/dotnet10-microservices-architect/SKILL.md` (read the relevant `references/*.md` before writing code for that concern):

- **Custom CQRS — no MediatR** (or any mediator package). Hand-rolled `ICommandDispatcher`/`IQueryDispatcher` resolving handlers from DI.
- **Clean Architecture, dependencies point inward.** API → Application → Domain; Infrastructure → Application + Domain. Domain references nothing (no EF, no ASP.NET). Application defines interfaces; Infrastructure implements them.
- **Database-first.** Scaffold EF entities from the existing PostgreSQL schema — the schema is authoritative, migrations only track drift. Don't reverse this.
- **Integration events (RabbitMQ) cross service boundaries; domain events stay inside one service** — translate at the Application boundary.
- Target topology (not yet built): 3 microservices + YARP gateway (the trust boundary: JWT validation + rate limiting at the edge) + Aspire AppHost.

## React frontend (when building it)

Governed by `.claude/skills/REACT_SKILL.md` — these are hard rules, not suggestions:

- Stack is **locked**: React 19.2 (pinned) + Vite 6, TanStack Router + Query v5, Zustand, react-hook-form + zod, Tailwind v4, Radix + cmdk + sonner. React Compiler on — **don't hand-write useMemo/useCallback** without a profiler trace.
- **Design tokens only** — zero hex literals in components (palette/motion in `:root`).
- **Right-side slide-over panel is the primary CRUD modality** — not centered modals, not page navigations. URL-addressable via search param.
- **Backend-driven nav** (above) — never gate UI on role in JSX.
- **Bilingual (en/hi)** — every user-facing label needs a Hindi string.
- Every list needs skeleton + empty + error states; POSTs carry an `Idempotency-Key` header; slot times always explicit `Asia/Kolkata`.

## Working conventions

- Adding a new product = add a new schema; **never** add product-specific columns to `platform.*` or `platform_api.*` (see `database/README.md` "Adding a New Product").
- Schema/RBAC/PHI/payout/encryption changes are security-sensitive: new tables must carry `tenant_id` + an RLS evaluation; payout *approval* must be a distinct permission from *execution*; patient-data reads declare purpose-of-use. The `security-compliance-auditor` agent holds veto on these.
- Commit cadence is the user's call — the project root isn't a git repo; only `backend/mediq/` is versioned (and currently has no commits).
