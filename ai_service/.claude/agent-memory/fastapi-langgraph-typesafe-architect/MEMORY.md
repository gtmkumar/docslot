# fastapi-langgraph-typesafe-architect — Memory Index

- [AI service gates](project_ai-service-gates.md) — ai_service enforces ruff + live-DB pytest, NOT mypy --strict; conftest seed constraint gotcha.
- [Prescription OCR: no consent gate](project_prescription-ocr-no-consent-gate.md) — /extractions/prescription deliberately skips enforce_phi_gate (ratified); don't re-add it.
- [Extraction: no caller paths](project_extraction-no-caller-paths.md) — sourceUrl vetoed; prescription forbids it (422), lab dev-gates it (400, flag default off).
