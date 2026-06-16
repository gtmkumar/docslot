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

logger = logging.getLogger("ai_service.ocr_repository")

DOCUMENT_EXTRACT_PERMISSION = "ai.documents.extract"


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
) -> str:
    """Insert one ai.ai_document_extractions row; return the extraction_id."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO ai.ai_document_extractions (
                tenant_id, source_type, source_url, source_mime_type,
                source_size_bytes, related_patient_id, related_booking_id,
                ocr_engine, raw_ocr_text, extraction_model, extracted_data,
                overall_confidence, requires_human_review, status
            ) VALUES (
                %(tenant_id)s, 'lab_report', %(source_url)s, %(mime)s,
                %(size)s, %(patient_id)s, %(booking_id)s,
                %(ocr_engine)s, %(raw)s, %(model)s, %(data)s,
                %(conf)s, %(review)s, %(status)s
            )
            RETURNING extraction_id
            """,
            {
                "tenant_id": tenant_id,
                "source_url": source_url,
                "mime": source_mime_type,
                "size": source_size_bytes,
                "patient_id": related_patient_id,
                "booking_id": related_booking_id,
                "ocr_engine": ocr_engine,
                "raw": raw_ocr_text,
                "model": extraction_model,
                "data": json.dumps(extracted_data),
                "conf": overall_confidence,
                "review": requires_human_review,
                "status": status,
            },
        )
        row = cur.fetchone()
        return str(row["extraction_id"])


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
# Compliance logging (best-effort) — mirrors rag_repository patterns.
# ---------------------------------------------------------------------------
_ALLOWED_PURPOSES = {
    "treatment", "follow_up", "emergency", "consultation",
    "research", "audit", "patient_request", "legal_obligation",
}


def log_purpose_of_use_best_effort(
    *, user_id: str, tenant_id: str, patient_id: str | None, purpose: str
) -> None:
    """Best-effort write to platform.purpose_of_use_log. Never blocks."""
    declared = purpose if purpose in _ALLOWED_PURPOSES else "treatment"
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.purpose_of_use_log (
                    user_id, tenant_id, accessed_resource_type,
                    accessed_resource_id, declared_purpose, purpose_notes
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, 'lab_report',
                    %(patient_id)s, %(declared)s, %(notes)s
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "patient_id": patient_id,
                    "declared": declared,
                    "notes": f"AI OCR lab-report extraction (raw purpose header: {purpose})",
                },
            )
    except Exception as exc:  # noqa: BLE001 — intentional best-effort
        logger.warning("purpose_of_use_log write failed (continuing): %s", exc)


def write_audit_best_effort(
    *, user_id: str, tenant_id: str, patient_id: str | None, action: str, purpose: str
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
                    %(user_id)s, %(tenant_id)s, %(action)s, 'lab_report', %(resource_id)s,
                    %(purpose)s, 'consent', true
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "action": action,
                    "resource_id": patient_id,
                    "purpose": purpose,
                },
            )
    except Exception as exc:  # noqa: BLE001 — intentional best-effort
        logger.warning("audit write failed (continuing): %s", exc)
