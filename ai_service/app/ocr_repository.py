"""Data access for the OCR lab-report extraction workflow.

PHI ISOLATION (owner connection bypasses RLS — scoping is THIS code's job):
  - Every read filters `tenant_id = <jwt tenant>`.
  - A supplied related_patient_id must be linked to the tenant via
    docslot.patient_tenant_links (the router 404s otherwise).
"""
from __future__ import annotations

import json
import logging

from .db import get_connection
from .encryption import EncryptionContext, FieldEncryptor

logger = logging.getLogger("ai_service.ocr_repository")

DOCUMENT_EXTRACT_PERMISSION = "ai.documents.extract"
# Raw OCR text is PHI (a lab report). It carries the 'medical_history' data_class
# (so it shares the tenant key + DPDP erasure with other clinical PHI); the
# break-glass/purpose resource_type for the gate is 'lab_report'.
_RESOURCE_TYPE = "lab_report"


def _enc_ctx(
    tenant_id: str,
    patient_id: str | None,
    user_id: str | None,
    resource_type: str = _RESOURCE_TYPE,
) -> EncryptionContext:
    return EncryptionContext(
        user_id=user_id,
        tenant_id=tenant_id,
        resource_type=resource_type,
        resource_id=patient_id,
    )


def patient_linked_to_tenant(tenant_id: str, patient_id: str) -> bool:
    """True iff the patient is linked to this tenant via patient_tenant_links."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT 1 FROM docslot.patient_tenant_links
            WHERE tenant_id = %(tenant_id)s AND patient_id = %(patient_id)s
            LIMIT 1
            """,
            {"tenant_id": tenant_id, "patient_id": patient_id},
        )
        return cur.fetchone() is not None


def insert_extraction(
    *,
    tenant_id: str,
    source_url: str,
    source_mime_type: str,
    source_size_bytes: int | None,
    related_patient_id: str | None,
    related_booking_id: str | None,
    ocr_engine: str,
    raw_ocr_text: str,
    extraction_model: str,
    extracted_data: dict,
    overall_confidence: float,
    requires_human_review: bool,
    status: str,
    source_type: str = "lab_report",
    resource_type: str = _RESOURCE_TYPE,
    user_id: str | None = None,
) -> str:
    """Insert one ai.ai_document_extractions row; return the extraction_id.

    raw_ocr_text is PHI and is envelope-ENCRYPTED at rest (encryption_key_id set);
    encryption + the forensic key_usage_log share this row's transaction. The
    structured extracted_data stays as queryable JSONB; the free-text PHI lives in
    raw_ocr_text, which is encrypted. related_patient_id is required by the router.

    source_type distinguishes 'lab_report' from 'prescription' (both share the
    medical_history encryption key); resource_type only labels the forensic
    key_usage_log ('lab_report' vs 'medical_history').
    """
    with get_connection() as conn:
        enc = FieldEncryptor(
            conn, _enc_ctx(tenant_id, related_patient_id, user_id, resource_type)
        )
        raw_payload, key_id = enc.encrypt_text(raw_ocr_text)
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO ai.ai_document_extractions (
                    tenant_id, source_type, source_url, source_mime_type,
                    source_size_bytes, related_patient_id, related_booking_id,
                    ocr_engine, raw_ocr_text, extraction_model, extracted_data,
                    overall_confidence, requires_human_review, status, encryption_key_id
                ) VALUES (
                    %(tenant_id)s, %(source_type)s, %(source_url)s, %(mime)s,
                    %(size)s, %(patient_id)s, %(booking_id)s,
                    %(ocr_engine)s, %(raw)s, %(model)s, %(data)s,
                    %(conf)s, %(review)s, %(status)s, %(key_id)s
                )
                RETURNING extraction_id
                """,
                {
                    "tenant_id": tenant_id,
                    "source_type": source_type,
                    "source_url": source_url,
                    "mime": source_mime_type,
                    "size": source_size_bytes,
                    "patient_id": related_patient_id,
                    "booking_id": related_booking_id,
                    "ocr_engine": ocr_engine,
                    "raw": raw_payload,
                    "model": extraction_model,
                    "data": json.dumps(extracted_data),
                    "conf": overall_confidence,
                    "review": requires_human_review,
                    "status": status,
                    "key_id": key_id,
                },
            )
            row = cur.fetchone()
            return str(row["extraction_id"])


def get_extraction_raw_text(
    extraction_id: str, tenant_id: str, user_id: str | None = None
) -> str | None:
    """Decrypt + return the raw OCR text for one extraction (tenant scoped).

    The only PHI read path for raw_ocr_text; each decrypt is logged to
    key_usage_log. Returns None if the extraction is not found in this tenant.
    """
    with get_connection() as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT raw_ocr_text, related_patient_id
                FROM ai.ai_document_extractions
                WHERE extraction_id = %(eid)s AND tenant_id = %(tid)s
                """,
                {"eid": extraction_id, "tid": tenant_id},
            )
            row = cur.fetchone()
        if row is None or row["raw_ocr_text"] is None:
            return None
        patient_id = str(row["related_patient_id"]) if row["related_patient_id"] else None
        enc = FieldEncryptor(conn, _enc_ctx(tenant_id, patient_id, user_id))
        return enc.decrypt_text(row["raw_ocr_text"])


def list_extractions(tenant_id: str, limit: int) -> list[dict]:
    """Recent extractions for this tenant (tenant scoped)."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT extraction_id, source_type, status, overall_confidence,
                   requires_human_review,
                   COALESCE((extracted_data->>'abnormalCount')::int, 0) AS abnormal_count,
                   created_at
            FROM ai.ai_document_extractions
            WHERE tenant_id = %(tenant_id)s
            ORDER BY created_at DESC
            LIMIT %(limit)s
            """,
            {"tenant_id": tenant_id, "limit": limit},
        )
        return list(cur.fetchall())


# ---------------------------------------------------------------------------
# Compliance logging
# ---------------------------------------------------------------------------
# NOTE: purpose-of-use logging moved to app/phi_access.record_purpose_of_use
# (FIRST-CLASS, break-glass-aware). The hash-chained audit_log write below stays
# best-effort (supplementary to the purpose-of-use record).
def write_audit_best_effort(
    *,
    user_id: str,
    tenant_id: str,
    patient_id: str | None,
    action: str,
    purpose: str,
    resource_type: str = "lab_report",
) -> None:
    """Best-effort hash-chained audit write. Never blocks."""
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.audit_log (
                    user_id, tenant_id, action, resource_type, resource_id,
                    purpose, legal_basis, success
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, %(action)s, %(resource_type)s, %(resource_id)s,
                    %(purpose)s, 'consent', true
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "action": action,
                    "resource_type": resource_type,
                    "resource_id": patient_id,
                    "purpose": purpose,
                },
            )
    except Exception as exc:  # noqa: BLE001 — intentional best-effort
        logger.warning("audit write failed (continuing): %s", exc)
