"""Agentic triage-routing endpoints. All require a valid DocSlot JWT.

Tenant isolation is enforced in code (owner connection bypasses RLS): every run,
step, and clinical lookup filters tenant_id = JWT tenant.

PHI compliance:
  - 401 if the bearer token is missing/invalid (via get_principal).
  - A pure free-text complaint (no patientId/bookingId) needs ONLY auth.
  - If patientId or bookingId is supplied (PHI access) the X-Purpose-Of-Use
    header is REQUIRED (400 if absent) and the patient/booking is tenant-scope
    verified (404 if cross-tenant / unknown). Best-effort purpose + audit logs.
"""
from __future__ import annotations

import logging
import uuid

from fastapi import APIRouter, Depends, Header, HTTPException, status

from ..auth import Principal, get_principal
from .. import triage, triage_repository as repo
from ..schemas import (
    SuggestedDoctor,
    TriageRequest,
    TriageResponse,
    TriageRunDetailResponse,
    TriageRunListItem,
    TriageRunListResponse,
    TriageRunStep,
    TriageStepSummary,
    TriageUrgency,
)

logger = logging.getLogger("ai_service.triage")
router = APIRouter(prefix="/triage", tags=["triage"])


def _validate_uuid_or_404(value: str) -> str:
    """Return canonical UUID string, or 404 (treat bad ids as not-found)."""
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError, TypeError):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Referenced resource not found in this tenant.",
        )


@router.post("", response_model=TriageResponse)
@router.post("/", response_model=TriageResponse, include_in_schema=False)
def triage_complaint(
    body: TriageRequest,
    principal: Principal = Depends(get_principal),
    x_purpose_of_use: str | None = Header(default=None, alias="X-Purpose-Of-Use"),
) -> TriageResponse:
    patient_id: str | None = None
    booking_id: str | None = None
    phi_resource_type: str | None = None
    phi_resource_id: str | None = None

    # PHI path: a patient/booking reference requires X-Purpose-Of-Use + tenant scope.
    if body.patientId or body.bookingId:
        purpose = (x_purpose_of_use or "").strip()
        if not purpose:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Missing required X-Purpose-Of-Use header for PHI access.",
            )

        if body.patientId:
            patient_id = _validate_uuid_or_404(body.patientId)
            if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail="Patient not found in this tenant.",
                )
            phi_resource_type, phi_resource_id = "patient", patient_id

        if body.bookingId:
            booking_id = _validate_uuid_or_404(body.bookingId)
            if not repo.booking_in_tenant(principal.tenant_id, booking_id):
                raise HTTPException(
                    status_code=status.HTTP_404_NOT_FOUND,
                    detail="Booking not found in this tenant.",
                )
            # Prefer patient as the audited resource; fall back to booking.
            phi_resource_type = phi_resource_type or "booking"
            phi_resource_id = phi_resource_id or booking_id

    result = triage.run_triage(
        tenant_id=principal.tenant_id,
        user_id=principal.user_id,
        complaint=body.complaint,
        patient_age=body.patientAge,
        patient_id=patient_id,
        booking_id=booking_id,
    )

    # Best-effort compliance logging only when PHI was actually referenced.
    if phi_resource_id:
        repo.log_purpose_of_use_best_effort(
            user_id=principal.user_id, tenant_id=principal.tenant_id,
            resource_type=phi_resource_type, resource_id=phi_resource_id,
            purpose=(x_purpose_of_use or "").strip(),
        )
        repo.write_audit_best_effort(
            user_id=principal.user_id, tenant_id=principal.tenant_id,
            resource_type=phi_resource_type, resource_id=phi_resource_id,
            purpose=(x_purpose_of_use or "").strip(),
        )

    return TriageResponse(
        runId=result["runId"],
        workflowKey=result["workflowKey"],
        symptoms=result["symptoms"],
        department=result["department"],
        urgency=TriageUrgency(
            band=result["urgency"]["band"],
            redFlags=result["urgency"]["redFlags"],
        ),
        suggestedDoctors=[SuggestedDoctor(**d) for d in result["suggestedDoctors"]],
        steps=[TriageStepSummary(**s) for s in result["steps"]],
    )


@router.get("/runs", response_model=TriageRunListResponse)
def list_runs(
    limit: int = 20,
    principal: Principal = Depends(get_principal),
) -> TriageRunListResponse:
    limit = max(1, min(limit, 100))
    rows = repo.list_runs(principal.tenant_id, limit)
    items: list[TriageRunListItem] = []
    for r in rows:
        out = r.get("output_data") or {}
        items.append(
            TriageRunListItem(
                runId=str(r["run_id"]),
                status=r["status"],
                department=out.get("department"),
                urgencyBand=(out.get("urgency") or {}).get("band"),
                createdAt=r["started_at"].isoformat() if r["started_at"] else "",
            )
        )
    return TriageRunListResponse(count=len(items), runs=items)


@router.get("/runs/{run_id}", response_model=TriageRunDetailResponse)
def get_run(
    run_id: str,
    principal: Principal = Depends(get_principal),
) -> TriageRunDetailResponse:
    run_id = _validate_uuid_or_404(run_id)
    run = repo.get_run(principal.tenant_id, run_id)
    if run is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Triage run not found in this tenant.",
        )

    out = run.get("output_data") or {}
    steps = [
        TriageRunStep(
            stepNumber=s["step_number"],
            nodeName=s["node_name"],
            stepType=s["step_type"],
            success=s["success"],
            durationMs=s["duration_ms"],
            toolInput=s["tool_input"],
            toolOutput=s["tool_output"],
        )
        for s in run["steps"]
    ]
    return TriageRunDetailResponse(
        runId=str(run["run_id"]),
        workflowKey=repo.WORKFLOW_KEY,
        status=run["status"],
        inputData=run["input_data"],
        outputData=run.get("output_data"),
        department=out.get("department"),
        urgencyBand=(out.get("urgency") or {}).get("band"),
        startedAt=run["started_at"].isoformat() if run["started_at"] else "",
        completedAt=run["completed_at"].isoformat() if run["completed_at"] else None,
        durationMs=run["duration_ms"],
        steps=steps,
    )
