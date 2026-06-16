---
name: python-ai-service
description: The Python AI sibling service (FastAPI, no-show prediction) — location, run, integration
metadata:
  type: project
---

The Python AI sibling service (CLAUDE.md's second service) exists at `ai_service/` (FastAPI, built 2026-06, verified end-to-end). Clean layering: `app/{config,db,auth,features,model,repository,main}.py` + `app/routers/{health,predictions}.py`.

**Run:** `cd ai_service && python3.13 -m venv .venv && .venv/bin/pip install -r requirements.txt && .venv/bin/uvicorn app.main:app --port 8088`. Config via `AI_`-prefixed env vars (`.env.example`).

**Shared identity (verified):** validates DocSlot HS256 JWTs — the SAME token minted by `POST :5054/api/v1/auth/login` is accepted by both the .NET API and this service. HMAC key = `base64.b64decode(SigningKey)` (the .NET side does `Convert.FromBase64String`); issuer `docslot-platform`, audience `docslot-clients`; claims `sub`/`tenant_id`.

**Endpoints:** `GET /health` (no auth); `POST /ai/v1/predictions/no-show {bookingId}`; `GET /ai/v1/predictions/no-show/today`. No-show model = sklearn LogisticRegression with a deterministic **heuristic fallback** when labeled samples < 20 (demo tenant has ~6, so it uses `v1-heuristic`). Writes `ai.ai_predictions` (+ best-effort hash-chained `platform.audit_log`). Security verified: 401 no/garbage token, 404 cross-tenant booking.

**Workflows built (all verified, same JWT + tenant-in-code + PHI gating):**
- **No-show prediction** — `POST /ai/v1/predictions/no-show`, `/today`. sklearn LogisticRegression + heuristic fallback (<20 labeled → `v1-heuristic`). Writes `ai.ai_predictions`.
- **RAG over medical history** — `POST /ai/v1/rag/index`, `/rag/ask`, `GET /rag/status`. Real semantic embeddings via **fastembed BAAI/bge-small-en-v1.5 (384-dim)** (offline sklearn HashingVectorizer fallback wired). Vectors stored as **bytea** in `ai.embeddings` (no pgvector column → app-side numpy cosine, top-k=4). Extractive answer + citations (optional LLM synth if `AI_LLM_API_KEY` set). PHI: requires `X-Purpose-Of-Use` header (400 without), permission `docslot.medical_history.read` via `platform.resolve_user_permissions`, patient-tenant gated via `patient_tenant_links` (404 cross-tenant), best-effort `purpose_of_use_log` + audit. Verified genuinely semantic ("antibiotics?" → Penicillin allergy).
- **OCR lab-report extraction** — `POST /ai/v1/extractions/lab-report`, `GET /ai/v1/extractions`. Tesseract 5.5.2 (`/opt/homebrew/bin/tesseract`) via pytesseract over a PIL-generated CBC sample (`ai_service/samples/`), parses analytes + LOW/HIGH flags → `ai.ai_document_extractions`. (Built Step 11.)

**Gotchas:**
- `ai.ai_predictions.prediction_type` CHECK requires `'no_show_probability'` (not `'no_show'`). `docslot.patient_medical_history.record_type` ∈ allergy/chronic_condition/surgery/medication/vaccination/family_history/lifestyle.
- Connects as DB owner `gtmkumar` (dev), enforces tenant isolation IN CODE (every query filters `tenant_id`), because `docslot_app` lacks ai-schema grants and granting was intentionally avoided (classifier-blocked). Production → dedicated least-privilege `docslot_ai` role + RLS + `SET LOCAL app.tenant_id`.
- Seeds for AI data: `seed_demo_medical_history.sql` (19 PHI records / 8 patients — the RAG corpus). Demo patient w/ rich history: Riya Kapoor `4df22406-e2ed-b59a-4275-961054176a85`.
- Still spec-only in `ai.*`: LangGraph multi-step triage (`ai_workflows`/`ai_agent_runs`/`ai_agent_steps`), `ai_feedback`. See [[live-stack-runbook]].
