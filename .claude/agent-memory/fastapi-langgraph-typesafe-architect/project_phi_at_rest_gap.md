---
name: phi-at-rest-gap
description: ai_service stores PHI plaintext in ai.embeddings / ai.ai_document_extractions despite schema mandating encryption
metadata:
  type: project
---

**Fact (as of 2026-06-16):** The DocSlot `ai_service` writes PHI in PLAINTEXT to columns the `database/06_ai_services.sql` schema explicitly documents as encrypted:
- `ai.embeddings.chunk_text` — schema comment "(encrypted)"; code writes raw history text.
- `ai.embeddings.embedding_vector` BYTEA — schema comment "Encrypted vector"; code writes raw float32 `.tobytes()`.
- `ai.embeddings.encryption_key_id` — never populated by the code.
- `ai.ai_document_extractions.raw_ocr_text` / `extracted_data` — raw lab values stored plaintext.

The schema also has `platform.encrypted_fields_registry` + `platform.encryption_keys` that this service ignores.

**Why:** Lab results and medical history are PHI under India's DPDP; "encrypted at rest" is asserted in schema comments and is a compliance expectation. The security-compliance-auditor holds veto on PHI paths.

**How to apply:** Any production-readiness pass on the AI service must route these writes through the platform field-encryption helpers (or the .NET encryption path) and populate `encryption_key_id`. Flag to security-compliance-auditor. Separately, the owner DB connection bypasses RLS by design, so every query MUST keep its `tenant_id = <jwt>` filter — that part the code does correctly today. See [[ai-service-shape]].
