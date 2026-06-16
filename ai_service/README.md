# DocSlot AI Service

The **Python AI sibling** to the DocSlot .NET transactional service. It shares the
same PostgreSQL system of record (`docslot_platform`), authenticates callers with
DocSlot-issued JWTs, and provides two working AI workflows:

1. **No-show prediction** (`ai.ai_predictions`).
2. **Semantic RAG over patient medical history** (`ai.embeddings` +
   `ai.ai_knowledge_bases`).

Both write best-effort to `platform.audit_log`; the PHI RAG workflow also writes
`platform.purpose_of_use_log`.

It does **not** own any data — the .NET service remains the system of record. This
service only reads bookings/slots/patients and inserts prediction rows.

## Architecture

Clean, small, typed modules under `app/`:

```
app/
  config.py              pydantic-settings Settings (env-overridable dev defaults)
  db.py                  context-managed psycopg connection helper + health probe
  auth.py                HS256 JWT validation -> Principal{user_id, tenant_id}
  schemas.py             pydantic request/response models
  features.py            explainable feature engineering + vectorization
  model.py               sklearn LogisticRegression OR heuristic fallback (lazy, cached)
  repository.py          tenant-scoped SQL (bookings, predictions, audit)
  embeddings.py          pluggable embedder: fastembed (semantic) OR HashingVectorizer
  rag.py                 RAG: indexing, numpy cosine retrieval, extractive/LLM answer
  rag_repository.py      tenant+patient-scoped SQL (history, embeddings, KB, PHI logs)
  routers/
    health.py            GET /health (no auth)
    predictions.py       POST /ai/v1/predictions/no-show, GET .../today
    rag.py               POST /ai/v1/rag/index, POST .../ask, GET .../status
  main.py                FastAPI wiring
```

## RAG over patient medical history

Semantic question-answering grounded in a patient's `docslot.patient_medical_history`.

**Embedding backend (pluggable, fixed 384-dim, reported per response):**

1. **`fastembed`** (PREFERRED) — a real semantic ONNX model
   (`BAAI/bge-small-en-v1.5`, 384-dim, L2-normalized). Downloaded on first use.
2. **`hashing`** (FALLBACK) — a fully-offline scikit-learn `HashingVectorizer`
   (`n_features=384`, `alternate_sign=False`) + L2 normalization. No download, no
   network — a fixed, reusable lexical embedding space. Used only when fastembed
   cannot initialize (offline / model download fails).

Both share the same dimension so a knowledge base indexed with one backend is
queried with that same backend consistently. The active backend is cached for the
process and reported in the `/rag/index` response (`backend`, `embeddingModel`).

**Storage.** `ai.embeddings` stores vectors as **bytea** (not pgvector): float32
numpy bytes (`<f4`). Similarity is therefore computed **app-side in numpy** — vectors
are L2-normalized at embed time, so cosine == dot product. Indexing is **idempotent**:
a chunk is keyed by `sha256(chunk_text)` and skipped if `(tenant_id, source_id,
chunk_text_hash)` already exists. Each indexed patient updates the tenant's
`ai.ai_knowledge_bases` row (`kb_key='patient_medical_history'`) with the doc count.

**Answer synthesis.** Extractive by default (offline): a concise, rank-ordered
summary stitched from the top-k (k=4) chunks, with citations. If `AI_LLM_API_KEY`
(or `AI_OPENAI_API_KEY`) is set, the answer MAY be synthesized by an LLM (httpx call,
strictly grounded in the retrieved chunks); on any failure it falls back to
extractive. The `mode` field reports `extractive` | `llm`.

### PHI compliance (medical history is PHI)

Every `/rag/index` and `/rag/ask` call enforces, in order:

- **401** — missing/invalid bearer JWT (`get_principal`).
- **400** — the `X-Purpose-Of-Use` header is absent (required for PHI access).
- **403** — caller lacks `docslot.medical_history.read`, resolved via the canonical
  `platform.resolve_user_permissions(user, tenant)`. Fail-closed: if the permission
  cannot be proven, access is denied.
- **404** — the patient is not linked to the JWT tenant
  (`docslot.patient_tenant_links`) **or** has no medical history. This is the
  cross-tenant guard.
- **Tenant + patient scoping in EVERY query.** `ai.embeddings` has **no RLS**, so
  every read filters BOTH `tenant_id = <jwt tenant>` AND `patient_id`. The owner
  connection bypasses RLS — these filters are the only isolation guard.
- **Best-effort compliance logging** on each access: `platform.purpose_of_use_log`
  (`accessed_resource_type='medical_history'`, declared purpose from the header,
  coerced to the allowed CHECK set) + hash-chained `platform.audit_log`
  (`action='ai.rag.index' | 'ai.rag.ask'`, `legal_basis='consent'`). Failures are
  logged and swallowed; they never block the request.

`/rag/status` is tenant-scoped (JWT tenant only) and not PHI-bearing, so it requires
only a valid JWT (no purpose header).

## No-show model

Two deterministic, **fully offline** paths (no external LLM / API keys):

1. **`v1-logreg`** — `LogisticRegression` trained on the tenant's historical labeled
   bookings (`status='no_show'` → 1; `completed`/`confirmed` → 0). Used only when
   labeled samples ≥ `AI_MIN_TRAINING_SAMPLES` (default 20) **and** both classes are
   present.
2. **`v1-heuristic`** — transparent, calibrated fallback for sparse tenants (the demo
   tenant has ~10 bookings / 1 no-show). Starts at the tenant base rate and applies
   bounded, explainable bumps (walk-in / new patient / long lead time / early morning
   / prior no-show rate), clamped to `[0.02, 0.95]`.

The chosen path is reported in `modelVersion` and reflected in `featuresUsed`. Models
are trained lazily and cached per tenant.

Features (all persisted to `features_used` for explainability): booked_via (one-hot),
hour-of-day, day-of-week, patient age, patient prior-no-show-rate, lead time
(booked_at → slot), has-notes, is-early-morning, is-new-patient.

## Endpoints (prefix `/ai/v1`)

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health` | none | `{status, service, dbConnected}` |
| POST | `/ai/v1/predictions/no-show` | Bearer | Body `{bookingId}`. Tenant-scoped lookup (404 if not in tenant). Scores, persists, returns `{bookingId, noShowProbability, riskBand, modelName, modelVersion, featuresUsed}`. |
| GET | `/ai/v1/predictions/no-show/today` | Bearer | Scores every `pending`/`confirmed` booking with `slot_date = today` for the JWT tenant; persists each; returns a list. |
| POST | `/ai/v1/rag/index` | Bearer + `X-Purpose-Of-Use` | Body `{patientId}`. Embeds + upserts the patient's medical-history rows (idempotent). 404 if the patient is unlinked to the tenant or has no history. Returns `{patientId, recordsIndexed, embeddingsTotal, embeddingModel, backend}`. |
| POST | `/ai/v1/rag/ask` | Bearer + `X-Purpose-Of-Use` | Body `{patientId, question}`. Cosine top-k (k=4) over the patient's embeddings (auto-indexes if none yet). Returns `{patientId, question, answer, mode, citations:[{historyId, recordType, title, severity, score}], retrieved}`. |
| GET | `/ai/v1/rag/status` | Bearer | Tenant-scoped: `{embeddings, patientsIndexed, knowledgeBases:[{kbKey, name, documentCount}]}`. |

`riskBand`: `low` (<0.34), `medium` (<0.67), `high` (≥0.67).

## Security / tenant isolation

- **JWT**: HS256, issuer `docslot-platform`, audience `docslot-clients`, signature +
  `exp` verified. The HMAC key is the **base64-decoded** bytes of the signing key
  (matches the .NET `Convert.FromBase64String(SigningKey)`). Missing/invalid token →
  **401**; valid token with no `tenant_id` claim → **403**.
- **Tenant isolation is enforced in code.** The dev connection uses the DB **owner**
  role (`gtmkumar`, local trust, no password) because the least-privilege
  `docslot_app` role lacks `ai.*` grants and we must not alter grants. The owner
  **bypasses RLS**, so every query in `repository.py` explicitly filters
  `tenant_id = <jwt tenant>` — that filter is the only isolation guard.
- **Production hardening** (documented, not done here): use a dedicated
  least-privilege `docslot_ai` role with RLS enabled and `SET LOCAL app.tenant_id`
  per transaction, so isolation is enforced by the database, not just the app.
- The audit write to the hash-chained `platform.audit_log` is **best-effort**: any
  failure is logged and swallowed so it never blocks a prediction.

## Run

```bash
cd ai_service
python3.13 -m venv .venv
.venv/bin/pip install -r requirements.txt

# Start (dev defaults connect to localhost docslot_platform as owner)
.venv/bin/uvicorn app.main:app --host 0.0.0.0 --port 8088

# Health
curl -s http://localhost:8088/health

# Mint a token from the running .NET API, then call:
TOKEN=$(curl -s http://localhost:5054/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"priyanka@apollocare.in","password":"reception"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

curl -s http://localhost:8088/ai/v1/predictions/no-show \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"bookingId":"<a-real-booking-id>"}'

curl -s http://localhost:8088/ai/v1/predictions/no-show/today \
  -H "Authorization: Bearer $TOKEN"

# --- RAG over patient medical history (PHI: needs X-Purpose-Of-Use) ---
RIYA=4df22406-e2ed-b59a-4275-961054176a85
curl -s http://localhost:8088/ai/v1/rag/index \
  -H "Authorization: Bearer $TOKEN" -H 'X-Purpose-Of-Use: treatment' \
  -H 'Content-Type: application/json' -d "{\"patientId\":\"$RIYA\"}"

curl -s http://localhost:8088/ai/v1/rag/ask \
  -H "Authorization: Bearer $TOKEN" -H 'X-Purpose-Of-Use: treatment' \
  -H 'Content-Type: application/json' \
  -d "{\"patientId\":\"$RIYA\",\"question\":\"Does this patient have any drug allergies?\"}"

curl -s http://localhost:8088/ai/v1/rag/status -H "Authorization: Bearer $TOKEN"
```

Config via env (prefix `AI_`), e.g. `AI_DATABASE_URL`, `AI_MIN_TRAINING_SAMPLES`.
Optional LLM synthesis for `/rag/ask`: `AI_LLM_API_KEY` (or `AI_OPENAI_API_KEY`),
`AI_LLM_BASE_URL`, `AI_LLM_MODEL` — unset means fully-offline extractive answers.
See `.env.example`.
