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

import base64
import binascii
import logging
import os
import tempfile
import uuid

from fastapi import APIRouter, Depends, HTTPException, Query, status

from ..auth import Principal, get_principal
from ..config import get_settings
from .. import ocr, ocr_repository as repo, phi_access, sample_docs
from ..schemas import (
    Analyte,
    ExtractionListItem,
    ExtractionListResponse,
    LabReportExtractRequest,
    LabReportExtractResponse,
    PrescriptionExtractRequest,
    PrescriptionExtractResponse,
    PrescriptionRecord,
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

    # A lab report is PHI: it MUST be patient-linked so it is never stored unlinked
    # and the consent gate + purpose-of-use record have a subject (accessed_resource_id
    # is NOT NULL). Reject an unlinked extraction (422) rather than storing orphan PHI.
    if not body.relatedPatientId:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="relatedPatientId is required for lab-report extraction (PHI must be patient-linked).",
        )
    patient_id = _validate_uuid_or_404(body.relatedPatientId, "Patient")
    if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient not found in this tenant.",
        )

    # Consent-or-break-glass gate BEFORE any PHI is extracted/persisted (403 if denied).
    grant = phi_access.enforce_phi_gate(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_LAB_REPORT,
        token_use=principal.token_use,
    )
    phi_access.record_purpose_of_use(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_LAB_REPORT,
        purpose=purpose,
        grant=grant,
    )

    booking_id: str | None = None
    if body.relatedBookingId:
        booking_id = _validate_uuid_or_404(body.relatedBookingId, "Booking")

    # Resolve the source image. A caller-supplied filesystem path (sourceUrl) is a
    # DEV-ONLY affordance: it is an arbitrary-local-file read + existence/size oracle,
    # so it is refused (400) unless AI_ALLOW_DEV_SOURCE_PATHS is explicitly enabled —
    # the path is never touched when the flag is off. With no sourceUrl the server
    # generates the sample (the normal path; the .NET proxy sends imageBase64/sample).
    source_url = body.sourceUrl
    if source_url:
        if not settings.allow_dev_source_paths:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="sourceUrl is disabled (dev-only). Submit the image inline instead.",
            )
        if not os.path.exists(source_url):
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=f"Source image not found at path: {source_url}",
            )
    else:
        source_url = sample_docs.generate_cbc_report()

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
        user_id=principal.user_id,
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


def _suffix_for(content_type: str | None, file_name: str | None) -> str:
    """Pick a temp-file suffix so PIL sniffs the format from the extension too."""
    if file_name and "." in file_name:
        return os.path.splitext(file_name)[1]
    mapping = {"image/jpeg": ".jpg", "image/jpg": ".jpg", "image/png": ".png", "image/webp": ".webp"}
    return mapping.get((content_type or "").lower(), ".jpg")


@router.post("/prescription", response_model=PrescriptionExtractResponse)
def extract_prescription(
    body: PrescriptionExtractRequest,
    principal: Principal = Depends(get_principal),
    purpose: str = Depends(require_purpose_of_use),
) -> PrescriptionExtractResponse:
    """OCR a paper prescription into medication records for human review.

    NO consent/break-glass gate (unlike the lab-report read): the image is
    CALLER-SUPPLIED — front-desk intake of a paper document the patient physically
    handed over, not a read of stored patient PHI. This mirrors the paper-Rx IMPORT
    write it feeds (a posture the security auditor ratified) and the .NET proxy in
    front of it. The access is still RECORDED (first-class purpose-of-use, no grant),
    the patient-link (422/404) and purpose-header gates still apply, the raw text is
    still encrypted at rest, and requires_human_review stays true — a human confirms
    every line before anything is saved.
    """
    settings = get_settings()

    # A non-human SERVICE identity is never a document-intake actor — refuse it (mirrors
    # the PHI-path service-token wall; orthogonal to the dropped consent gate).
    if principal.token_use == "service":
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="A service identity may not perform prescription intake.",
        )

    # PHI must be patient-linked: reject an unlinked extraction (422) — never orphan PHI.
    if not body.relatedPatientId:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="relatedPatientId is required for prescription extraction (PHI must be patient-linked).",
        )
    patient_id = _validate_uuid_or_404(body.relatedPatientId, "Patient")
    if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient not found in this tenant.",
        )

    # No consent gate (see docstring), but STILL record a first-class purpose-of-use
    # entry for the access — grant=None logs it on a consent basis (is_break_glass=false).
    phi_access.record_purpose_of_use(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
        purpose=purpose,
        grant=None,
    )

    # Resolve the source image: inline base64 (primary) or a server-generated sample.
    # NO caller-supplied filesystem path (auditor veto) — a base64-decoded photo is
    # written to a temp file the OCR engine reads, then removed in the finally below.
    temp_path: str | None = None
    source_mime = body.contentType or "image/jpeg"
    if body.imageBase64:
        try:
            image_bytes = base64.b64decode(body.imageBase64, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="imageBase64 is not valid base64.",
            ) from exc
        fd, temp_path = tempfile.mkstemp(suffix=_suffix_for(body.contentType, body.fileName))
        with os.fdopen(fd, "wb") as fh:
            fh.write(image_bytes)
        source_path = temp_path
        source_url = body.fileName or "prescription-upload"
    else:
        source_path = sample_docs.generate_prescription_sample()
        source_url = source_path
        source_mime = "image/png"

    try:
        try:
            result = ocr.extract_prescription(source_path)
        except Exception as exc:  # noqa: BLE001
            logger.exception("Prescription OCR extraction failed")
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                detail=f"OCR extraction failed: {exc}",
            ) from exc

        records = result["records"]
        confidence = result["overall_confidence"]
        doctor_name = result["external_doctor_name"]
        recorded_date = result["recorded_date"]

        extracted_data = {
            "records": records,
            "externalDoctorName": doctor_name,
            "recordedDate": recorded_date,
            "medicationCount": len(records),
            "abnormalCount": 0,  # keeps the list view's abnormalCount projection defined
        }

        size_bytes = os.path.getsize(source_path) if os.path.exists(source_path) else None

        extraction_id = repo.insert_extraction(
            tenant_id=principal.tenant_id,
            source_url=source_url,
            source_mime_type=source_mime,
            source_size_bytes=size_bytes,
            related_patient_id=patient_id,
            related_booking_id=None,
            ocr_engine=settings.ocr_engine,
            raw_ocr_text=result["raw_text"],
            extraction_model=settings.labparser_model,
            extracted_data=extracted_data,
            overall_confidence=confidence,
            requires_human_review=True,  # a human ALWAYS reviews a prescription before save
            status="extracted",
            source_type="prescription",
            resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
            user_id=principal.user_id,
        )
    finally:
        if temp_path and os.path.exists(temp_path):
            os.remove(temp_path)

    repo.write_audit_best_effort(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        action="ai.ocr.extract",
        purpose=purpose,
        resource_type="prescription",
    )

    return PrescriptionExtractResponse(
        extractionId=extraction_id,
        overallConfidence=confidence,
        externalDoctorName=doctor_name,
        recordedDate=recorded_date,
        records=[PrescriptionRecord(**r) for r in records],
        rawText=result["raw_text"],
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
