# DocSlot — WhatsApp-First Healthcare Booking Platform

> **Production specification and database for DocSlot**, a multi-tenant SaaS for appointment booking, prescriptions, and ABDM-integrated medical records in the Indian healthcare market. Two-service architecture: .NET (transactional) + Python (AI/ML).

## What's here

```
/
├── INDEX.md                    ← you are here
├── PRODUCTION_SPEC.md          The full product specification (260 KB)
├── AI_ARCHITECTURE.md          Python AI service tier design (DocSlot.AI)
├── AGENT_TEAM.md               Multi-agent build strategy (1 orchestrator + 13 specialists)
├── CONTEXT_MANAGEMENT.md       Context window discipline (5 mechanisms)
├── DEPLOYMENT.md               Zero-Dockerfile deployment guide + free CI setup
├── FUTURE_PRODUCTS.md          Adjacent product opportunities
├── REACT_SKILL.md              React 19.2 UI/UX skill: design DNA + 15 patterns
├── DATA_DICTIONARY.md          A-Z reference: all 113 tables + 1,627 columns
├── REQUIREMENTS.md             Enterprise PRD: FR/NFR with traceability
├── ARCHITECTURE.md             System architecture + ADR index
├── SECURITY.md                 Threat model, DPDP mapping, security gates
├── TESTING_STRATEGY.md         Test pyramid, CI gates, never-regress cases
├── API_GUIDELINES.md           REST conventions, idempotency, webhooks
├── CHANGELOG.md                Versioned history
├── COMMISSION_SYSTEM.md        Broker referral economy: legal positioning, attribution, payouts
├── RBAC_Navigation_PaaS_PostgreSQL.md  (repo root) Backend-driven menus, overrides, permission resolver — the shipped narrative
├── docs/RBAC.md                RBAC canonical model: tables, permission inventory, roles, decision semantics, R1–R6 hardening
├── docs/RBAC_FLOW.md           How one live request is authorized through the .NET pipeline (companion to RBAC.md)
├── .agents/                    Project memory (commit this to git)
│   ├── memory/                 7 memory files (decisions, API contracts, etc.)
│   ├── checkpoints/            Session checkpoints (auto-generated)
│   └── sessions/               Session transcripts (auto-generated)
├── agents/                     13 Claude Code subagent definitions
│   ├── docslot-orchestrator.md          Tier 0: coordinator
│   ├── database-architect.md            Tier 1: schema, RLS, EF Core
│   ├── platform-rbac-engineer.md        Tier 1: permissions, audit, encryption
│   ├── dotnet-clean-arch.md             Tier 2: .NET backend
│   ├── whatsapp-integration.md          Tier 2: Meta WhatsApp Cloud API
│   ├── abdm-integration.md              Tier 2: ABHA, HFR, HPR, FHIR R4
│   ├── platform-api-engineer.md         Tier 2: OAuth, webhooks
│   ├── langgraph-workflow-builder.md    Tier 3: Python AI workflows
│   ├── rag-embedding-engineer.md        Tier 3: RAG, pgvector, embeddings
│   ├── ml-analytics-engineer.md         Tier 3: pandas, sklearn, OCR, Whisper
│   ├── react-ui-engineer.md           Tier 4: React 19.2 frontend
│   ├── devops-infra-engineer.md         Tier 4: Docker, K8s, CI/CD
│   ├── qa-test-engineer.md              Tier Q: testing across all waves
│   └── security-compliance-auditor.md   Tier Q: DPDP, audit chain, OWASP
├── database/                   Canonical PostgreSQL schema — start here for any DB work
│   ├── README.md               Database architecture, RBAC model, execution order
│   ├── 01_platform_core.sql    Shared platform: tenants, users, RBAC, audit (18 tables)
│   ├── 02_platform_api.sql     Platform-as-a-Service: OAuth, webhooks (8 tables)
│   ├── 03_docslot.sql          DocSlot product schema (26 tables)
│   ├── 04_future_products.sql  RuralReach + SafeHer + GenericFirst (22 tables, optional)
│   ├── 05_security_hardening.sql  Encryption, audit chain, RLS, anomaly detection (13 tables)
│   ├── 06_ai_services.sql      AI/ML schema: LangGraph, RAG, OCR, predictions (10 tables)
│   ├── 07_commission_broker.sql  Broker referral + commission system (10 tables, PCPNDT-compliant)
│   ├── 08_rbac_navigation.sql    Backend-driven menus + per-user overrides (5 tables)
│   ├── 09_chat_identity.sql      WA chat memory, behalf consent, direct discount (1 table)
│   └── docslot_complete.sql    All 9 files bundled — runs in one psql command
└── archive/
    ├── DocSlot_PRD.docx        Early PRD v1 (historical only)
    └── DocSlot_PRD_v2.docx     Early PRD v2 (historical only)
```

**Total: 113 PostgreSQL tables across 8 schemas, designed for DPDP Act 2023 compliance with medical-grade security and native AI integration.**

## The two-service architecture

```
                        Patient (WhatsApp)
                                |
                                v
                +-----------------------------+
                | Meta WhatsApp Cloud API     |
                +-----------------------------+
                                |
                                v
        +-------------------------------------------+
        | DocSlot.API (.NET 10, React 19.2)         |
        | - Booking, billing, audit, RBAC            |
        | - System of record                         |
        | - EF Core, MediatR, Hangfire               |
        +-------------------------------------------+
                  |                       ^
                  | REST + events         | results
                  v                       |
        +-------------------------------------------+
        | DocSlot.AI (Python 3.12, FastAPI)         |
        | - LangGraph workflows (triage, safety)    |
        | - LangChain RAG (medical history Q&A)     |
        | - pandas / scikit-learn (predictions)     |
        | - PaddleOCR (lab reports)                 |
        | - Whisper (Hindi voice notes)             |
        +-------------------------------------------+
                                |
                                v
        +-------------------------------------------+
        | Shared PostgreSQL 16 + pgvector           |
        | Same tenants, users, RBAC, audit chain    |
        | RLS policies enforce isolation            |
        +-------------------------------------------+
```

Both services share PostgreSQL, share `platform.users` for authentication, share `platform.audit_log` for compliance. They scale independently. See `AI_ARCHITECTURE.md` for the full Python service design.

## Where to start

### If you're a developer building DocSlot.API (.NET)

1. Read `PRODUCTION_SPEC.md` Section 1 through 1.5 — understand the product and the 4 problems it solves.
2. Read `database/README.md` — understand multi-tenant platform, RBAC, security layers.
3. Run the SQL files in order against a fresh PostgreSQL 16+ database with pgvector:
   ```bash
   psql -d docslot_dev -f database/01_platform_core.sql
   psql -d docslot_dev -f database/02_platform_api.sql
   psql -d docslot_dev -f database/03_docslot.sql
   psql -d docslot_dev -f database/05_security_hardening.sql
   psql -d docslot_dev -f database/06_ai_services.sql  # If integrating with AI service
   ```
4. Read `SKILL.md` if working on the React frontend.

### If you're a developer building DocSlot.AI (Python)

1. Read `AI_ARCHITECTURE.md` end to end — defines what the service does and doesn't do.
2. Read `database/06_ai_services.sql` — the AI schema you'll be reading/writing from Python.
3. Read `database/05_security_hardening.sql` — encryption keys and audit chain you'll be using.
4. Build the FastAPI service following the directory structure in `AI_ARCHITECTURE.md`.

### If you want to build with Claude Code agents

1. Read `AGENT_TEAM.md` — full multi-agent build strategy
2. Copy `agents/*.md` to `.claude/agents/` in your project root
3. Start every multi-domain task with `docslot-orchestrator`
4. Single-domain trivial tasks can go directly to the relevant specialist

### If you're a Claude Code subagent

Your priority order for sources of truth:

| What you're building | Read this first |
|---------------------|-----------------|
| Database tables, indexes, RLS policies, RBAC seeds | `database/*.sql` (canonical) |
| EF Core entities (C#) | `PRODUCTION_SPEC.md` Appendix A |
| Request/Response DTOs and validation | `PRODUCTION_SPEC.md` Appendix B |
| WhatsApp integration code | `PRODUCTION_SPEC.md` Appendix D, E |
| Python AI service code | `AI_ARCHITECTURE.md` |
| LangGraph workflow logic | `AI_ARCHITECTURE.md` + `database/06_ai_services.sql` |
| React components | `SKILL.md` |

**Critical rule**: If a SQL CREATE TABLE block in `PRODUCTION_SPEC.md` conflicts with `database/*.sql`, the SQL file wins.

## Project status

- ✅ Product specification complete
- ✅ Database schema complete (113 tables, 11 SQL files)
- ✅ RBAC model designed and seeded
- ✅ Platform-as-a-Service API layer designed
- ✅ Security hardening designed (DPDP Act 2023 compliant)
- ✅ AI services schema and Python architecture designed
- ✅ Future product opportunities documented
- ✅ UI/UX skill for React frontend
- ⏭️ Implementation (Phase 1-5 + Python service)
