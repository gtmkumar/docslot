---
name: extraction-no-caller-paths
description: ai_service extraction endpoints must never OCR a caller-supplied filesystem path (auditor veto)
metadata:
  type: project
---

The OCR extraction endpoints must NEVER resolve a caller-supplied value to a local filesystem path. Security auditor VETOED the original `sourceUrl` parameter (2026-07-02 OCR round): an authenticated caller could point it at any file the process can read → arbitrary-local-file OCR read (not tenant-scoped) + a file existence/size oracle. The .NET proxy never forwards `sourceUrl` (it sends `imageBase64`), so it had no production purpose.

Current posture:
- `POST /ai/v1/extractions/prescription`: NO `sourceUrl` field at all. `PrescriptionExtractRequest` uses `model_config = ConfigDict(extra="forbid")`, so a stray `sourceUrl` (or any path-like extra) → **422**. Input is `imageBase64` (inline) or nothing → server-generated sample.
- `POST /ai/v1/extractions/lab-report`: `sourceUrl` is DEV-ONLY, gated behind settings flag `allow_dev_source_paths` (env `AI_ALLOW_DEV_SOURCE_PATHS`, **default False**). Flag off + `sourceUrl` present → **400**, path never touched. No-`sourceUrl` sample generation always works.

**How to apply:** Do not reintroduce a caller-controlled path input on any extraction endpoint. Keep `extra="forbid"` on the prescription request. Production must leave `allow_dev_source_paths=False`. Decoded inline images go to `tempfile.mkstemp` (0600) + `finally` cleanup (ruled sound). See [[prescription-ocr-no-consent-gate]], [[ai-service-gates]].
