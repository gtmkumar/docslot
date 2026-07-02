---
name: ai-service-gates
description: Enforced CI/dev gates for the DocSlot Python ai_service (ruff + live-DB pytest, NOT mypy --strict)
metadata:
  type: project
---

The `ai_service` (FastAPI, `/Users/gtmkumar/Documents/source/docslot/ai_service`) enforces **ruff + pytest**, NOT `mypy --strict`.

**Why:** `mypy --strict app` reports ~129 pre-existing errors (bare `dict`/`list`, untyped 3rd-party like sklearn/fastembed, `ndarray` type-args) across the shipped modules — the codebase deliberately uses bare `dict` return types (e.g. `ocr.py`, `routers/extractions.py`). There is no mypy config file; `.mypy_cache` exists but strict is not a gate. `ruff check` passes clean and IS the style gate.

**How to apply:** Match the existing style (bare `dict` in OCR/parser payloads is fine and consistent) rather than forcing full `--strict` compliance that would clash with 18 other files. Green gate = `ruff check app tests` clean + `python -m pytest` green (48 tests as of 2026-07-02). Tests run against the LIVE `docslot_platform` dev DB (owner conn, bypasses RLS; conftest seeds a fixed test tenant `aaaaaaaa-…0001`). Use in-process `TestClient(app)` — they do NOT need port 8000, so a running uvicorn can stay up.

Note: conftest medical-history seed must set `verified_by_user_id`+`verified_at` because `chk_history_clinic_rows_verified` (added by the paper-Rx work) requires clinic-source rows to be verified; the CHECK is evaluated on the candidate row before `ON CONFLICT DO NOTHING` resolves. See [[ai-service-phi-extraction-pattern]].
