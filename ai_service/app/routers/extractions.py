"""OCR lab-report extraction endpoints. All require a valid DocSlot JWT.

PHI compliance (lab reports are PHI):
  - 401 if the bearer token is missing/invalid (via get_principal).
  - 400 if the `X-Purpose-Of-Use` header is absent on the extract endpoint
    (reuses require_purpose_of_use from the rag router).
  - 404 if a supplied relatedPatientId is not linked to the JWT tenant.
  - Tenant scoping is enforced in EVERY query.
  - Best-effort purpose_of_use_log + hash-chained audit on each PHI access.
"""
from __future__ import annotations

import logging
import os
import uuid

from fastapi import APIRouter, Depends, HTTPException, Query, status

from ..auth import Principal, get_principal
from ..config import get_settings
from .. import ocr, ocr_repository as repo, sample_docs
from ..schemas import (
    Analyte,
    ExtractionListItem,
    ExtractionListResponse,
    LabReportExtractRequest,
    LabReportExtractResponse,
)
from .rag import require_purpose_of_use  # reuse the existing PHI dependency

logger = logging.getLogger("ai_service.extractions")
router = APIRouter(prefix="/extractions", tags=["extractions"])


def _validate_uuid_or_404(value: str, what: str) -> str:
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError, TypeError):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"{what} not found in this tenant.",
        )


@router.post("/lab-report", response_model=LabReportExtractResponse)
def extract_lab_report(
    body: LabReportExtractRequest,
    principal: Principal = Depends(get_principal),
    purpose: str = Depends(require_purpose_of_use),
) -> LabReportExtractResponse:
    settings = get_settings()

    # Cross-tenant guard for a supplied patient.
    patient_id: str | None = None
    if body.relatedPatientId:
        patient_id = _validate_uuid_or_404(body.relatedPatientId, "Patient")
        if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND,
                detail="Patient not found in this tenant.",
            )
    booking_id: str | None = None
    if body.relatedBookingId:
        booking_id = _validate_uuid_or_404(body.relatedBookingId, "Booking")

    # Resolve the source image: explicit path, or generate the sample.
    source_url = body.sourceUrl
    if not source_url:
        source_url = sample_docs.generate_cbc_report()
    if not os.path.exists(source_url):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Source image not found at path: {source_url}",
        )

    # OCR + parse.
    try:
        result = ocr.extract_lab_report(source_url)
    except Exception as exc:  # noqa: BLE001
        logger.exception("OCR extraction failed")
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=f"OCR extraction failed: {exc}",
        ) from exc

    analytes = result["analytes"]
    abnormal_count = result["abnormal_count"]
    confidence = result["overall_confidence"]

    requires_review = (
        abnormal_count > 0
        or confidence < settings.ocr_review_confidence_threshold
    )
    # status CHECK allows: pending/processing/extracted/reviewed/approved/failed.
    db_status = "extracted"

    extracted_data = {
        "panel": result["panel"],
        "analytes": analytes,
        "abnormalCount": abnormal_count,
        "hasCriticalFindings": abnormal_count > 0,
    }

    size_bytes = os.path.getsize(source_url) if os.path.exists(source_url) else None

    extraction_id = repo.insert_extraction(
        tenant_id=principal.tenant_id,
        source_url=source_url,
        source_mime_type="image/png",
        source_size_bytes=size_bytes,
        related_patient_id=patient_id,
        related_booking_id=booking_id,
        ocr_engine=settings.ocr_engine,
        raw_ocr_text=result["raw_text"],
        extraction_model=settings.labparser_model,
        extracted_data=extracted_data,
        overall_confidence=confidence,
        requires_human_review=requires_review,
        status=db_status,
    )

    repo.log_purpose_of_use_best_effort(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        purpose=purpose,
    )
    repo.write_audit_best_effort(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        action="ai.ocr.extract",
        purpose=purpose,
    )

    raw = result["raw_text"]
    preview = raw[:400] + ("..." if len(raw) > 400 else "")

    return LabReportExtractResponse(
        extractionId=extraction_id,
        sourceUrl=source_url,
        ocrEngine=settings.ocr_engine,
        overallConfidence=confidence,
        requiresHumanReview=requires_review,
        analytes=[Analyte(**a) for a in analytes],
        abnormalCount=abnormal_count,
        rawTextPreview=preview,
    )


@router.get("", response_model=ExtractionListResponse)
def list_extractions(
    principal: Principal = Depends(get_principal),
    limit: int = Query(default=20, ge=1, le=200),
) -> ExtractionListResponse:
    rows = repo.list_extractions(principal.tenant_id, limit)
    items = [
        ExtractionListItem(
            extractionId=str(r["extraction_id"]),
            sourceType=r["source_type"],
            status=r["status"],
            overallConfidence=(
                float(r["overall_confidence"]) if r["overall_confidence"] is not None else None
            ),
            requiresHumanReview=bool(r["requires_human_review"]),
            abnormalCount=int(r["abnormal_count"]),
            createdAt=r["created_at"].isoformat(),
        )
        for r in rows
    ]
    return ExtractionListResponse(count=len(items), extractions=items)
