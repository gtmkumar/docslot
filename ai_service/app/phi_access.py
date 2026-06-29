"""Clinical-consent gate + purpose-of-use logging for the AI PHI paths.

Replicates the .NET clinical-read contract (ReportAndAbdmFeatures /
PrescriptionFeatures): before the RAG/OCR paths embed, retrieve, or persist
patient PHI, the caller must have either the patient's active DPDP consent OR an
active, scoped, non-expired break-glass grant — otherwise the request is refused
(403). Every PHI access then writes a FIRST-CLASS platform.purpose_of_use_log row
(not best-effort): a break-glass access stamps is_break_glass=true +
review_required=true so it lands in v_security_review_queue.

Consent is keyed to the GLOBAL patient identity (docslot.patients has no
tenant_id — phone is the cross-tenant identity), exactly like the .NET
HasActiveConsent check; the break-glass grant is tenant-scoped.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

from fastapi import HTTPException, status

from .db import get_connection

# resource_type values for the break-glass grant + purpose log.
RESOURCE_MEDICAL_HISTORY = "medical_history"  # RAG over patient_medical_history
RESOURCE_LAB_REPORT = "lab_report"  # OCR lab-report extraction

_ALLOWED_PURPOSES = {
    "treatment", "follow_up", "emergency", "consultation",
    "research", "audit", "patient_request", "legal_obligation",
}

_CONSENT_DENIED_DETAIL = "Patient has no active consent; clinical read refused (DPDP)."


@dataclass(frozen=True)
class BreakGlassGrant:
    grant_id: str
    patient_id: str
    resource_type: str
    resource_id: str | None
    justification: str
    expires_at: datetime


def has_active_consent(patient_id: str) -> bool:
    """True iff the patient has an active, non-withdrawn DPDP consent on file.

    CROSS-TENANT by design: docslot.patients is global (no tenant_id); consent is
    keyed to the patient's global identity. Mirrors Patient.HasActiveConsent
    (consent_given_at present AND is_active AND not soft-deleted).
    """
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT (consent_given_at IS NOT NULL AND is_active AND deleted_at IS NULL)
                   AS active
            FROM docslot.patients
            WHERE patient_id = %(pid)s
            """,
            {"pid": patient_id},
        )
        row = cur.fetchone()
    return bool(row and row["active"])


def active_break_glass_grant(
    *,
    user_id: str,
    tenant_id: str,
    patient_id: str,
    resource_type: str,
    resource_id: str | None = None,
) -> BreakGlassGrant | None:
    """Resolve an active (non-revoked, non-expired) break-glass grant, or None.

    Exact mirror of BreakGlassService.GetActiveGrantAsync: a NULL resource_id
    (patient-wide access) matches only a patient-wide grant; a specific
    resource_id matches a patient-wide grant OR one scoped to that resource.
    """
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT grant_id, patient_id, resource_type, resource_id, justification, expires_at
            FROM platform.break_glass_grants
            WHERE user_id = %(u)s AND tenant_id = %(t)s AND patient_id = %(p)s
              AND resource_type = %(rt)s
              AND revoked_at IS NULL AND expires_at > NOW()
              AND (
                    (%(rid)s::uuid IS NULL AND resource_id IS NULL)
                 OR (%(rid)s::uuid IS NOT NULL AND (resource_id IS NULL OR resource_id = %(rid)s::uuid))
              )
            ORDER BY expires_at DESC
            LIMIT 1
            """,
            {"u": user_id, "t": tenant_id, "p": patient_id, "rt": resource_type, "rid": resource_id},
        )
        row = cur.fetchone()
    if row is None:
        return None
    return BreakGlassGrant(
        grant_id=str(row["grant_id"]),
        patient_id=str(row["patient_id"]),
        resource_type=row["resource_type"],
        resource_id=str(row["resource_id"]) if row["resource_id"] is not None else None,
        justification=row["justification"],
        expires_at=row["expires_at"],
    )


def enforce_phi_gate(
    *,
    user_id: str,
    tenant_id: str,
    patient_id: str,
    resource_type: str,
    resource_id: str | None = None,
) -> BreakGlassGrant | None:
    """Consent-or-break-glass gate. Returns the grant used (None = consented).

    Raises 403 (fail-closed) if the patient has no active consent AND no active
    break-glass grant. Call BEFORE any embed/retrieve/persist of PHI.
    """
    if has_active_consent(patient_id):
        return None
    grant = active_break_glass_grant(
        user_id=user_id,
        tenant_id=tenant_id,
        patient_id=patient_id,
        resource_type=resource_type,
        resource_id=resource_id,
    )
    if grant is None:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=_CONSENT_DENIED_DETAIL)
    return grant


def record_purpose_of_use(
    *,
    user_id: str,
    tenant_id: str,
    patient_id: str,
    resource_type: str,
    purpose: str,
    grant: BreakGlassGrant | None,
) -> None:
    """First-class platform.purpose_of_use_log write for a PHI access.

    NOT best-effort: a break-glass access MUST leave a tamper-evident,
    review-flagged record (it is the only row that proves a consent-override
    occurred and the sole driver of v_security_review_queue). Call BEFORE the PHI
    access so the access is recorded even if the downstream persist/retrieve dies.
    """
    if grant is not None:
        declared = "emergency"
        is_break_glass = True
        break_glass_reason: str | None = grant.justification
        review_required = True
        notes = f"AI break-glass PHI access (grant {grant.grant_id}; raw purpose header: {purpose})"
    else:
        declared = purpose if purpose in _ALLOWED_PURPOSES else "treatment"
        is_break_glass = False
        break_glass_reason = None
        review_required = False
        notes = f"AI consented PHI access (raw purpose header: {purpose})"

    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO platform.purpose_of_use_log (
                user_id, tenant_id, accessed_resource_type, accessed_resource_id,
                declared_purpose, purpose_notes, is_break_glass, break_glass_reason,
                review_required
            ) VALUES (
                %(uid)s, %(tid)s, %(rtype)s, %(rid)s,
                %(declared)s, %(notes)s, %(bg)s, %(reason)s,
                %(review)s
            )
            """,
            {
                "uid": user_id,
                "tid": tenant_id,
                "rtype": resource_type,
                "rid": patient_id,
                "declared": declared,
                "notes": notes,
                "bg": is_break_glass,
                "reason": break_glass_reason,
                "review": review_required,
            },
        )
