---
name: ai-service-shape
description: Architecture/typing conventions of the DocSlot Python ai_service as built (as of 2026-06-16 review)
metadata:
  type: project
---

The DocSlot `ai_service/` (FastAPI sibling) as built at 2026-06-16.

**Fact:** It is fully SYNCHRONOUS (zero `async def`), uses `app/main.py` module-level `app = FastAPI()` (no app factory / lifespan), psycopg3 with a `@contextmanager get_connection()` using `row_factory=dict_row` (one connection per query, no pool). Routers live in `app/routers/` (health, predictions, rag, extractions, triage); core logic in sibling modules `app/triage.py`, `app/rag.py`, `app/ocr.py`, `app/model.py` with matching `*_repository.py` data-access modules.

**Fact:** Despite the agent's mandate, there is NO LangGraph/LangChain — not installed, not imported. Triage is a hand-rolled deterministic "mini-langgraph" of plain `dict`-state node functions (`node_extract_symptoms` etc.) in `app/triage.py`. State is untyped `dict`, not a TypedDict.

**Fact:** Typing is loose — bare `dict`/`ndarray` everywhere; `mypy --strict` reports ~103 errors (mostly `[type-arg]` bare-dict, plus psycopg `fetchone()` tuple-vs-dict false-positives that are runtime-safe due to dict_row). No pyproject.toml / mypy config / pandas-stubs. pandas is NOT used (numpy + sklearn only).

**Why:** This shapes any review/extension — it is a working offline-first demo, not the strict-typed multi-agent target.

**How to apply:** When asked to harden or extend it, expect to (1) add a TypedDict triage state, (2) add pyproject with mypy strict config, (3) type psycopg rows via a `Row = dict[str, Any]` alias or TypedDicts, (4) decide whether real LangGraph is in scope. See [[phi-at-rest-gap]] for the security gap.
