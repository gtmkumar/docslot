"""RAG-over-patient-medical-history endpoints. All require a valid DocSlot JWT.

PHI compliance (medical history is PHI):
  - 401 if the bearer token is missing/invalid (via get_principal).
  - 400 if the `X-Purpose-Of-Use` header is absent on the two PHI endpoints.
  - 403 if the caller lacks `docslot.medical_history.read` (resolved via
    platform.resolve_user_permissions — the canonical RBAC path).
  - 404 if the patient is not linked to the JWT tenant (cross-tenant) or has no
    history. Tenant + patient scoping is enforced in EVERY query.
  - Best-effort purpose_of_use_log + hash-chained audit on each PHI access.
"""
from __future__ import annotations

import logging
import uuid

from fastapi import APIRouter, Depends, Header, HTTPException, status

from ..auth import Principal, get_principal
from .. import phi_access, rag, rag_repository as repo
from ..schemas import (
    KnowledgeBaseInfo,
    RagAskRequest,
    RagAskResponse,
    RagIndexRequest,
    RagIndexResponse,
    RagStatusResponse,
)

logger = logging.getLogger("ai_service.rag")
router = APIRouter(prefix="/rag", tags=["rag"])


def require_purpose_of_use(
    x_purpose_of_use: str | None = Header(default=None, alias="X-Purpose-Of-Use"),
) -> str:
    """PHI gate: the X-Purpose-Of-Use header MUST be present. 400 if absent."""
    if not x_purpose_of_use or not x_purpose_of_use.strip():
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Missing required X-Purpose-Of-Use header for PHI access.",
        )
    return x_purpose_of_use.strip()


def _validate_uuid(value: str) -> str:
    """Return canonical UUID string, or 404 (treat bad ids as not-found)."""
    try:
        return str(uuid.UUID(value))
    except (ValueError, AttributeError, TypeError):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient not found in this tenant.",
        )


def _gate_permission(principal: Principal) -> None:
    """Enforce the medical-history read permission via the canonical RBAC path."""
    try:
        allowed = repo.has_permission(
            principal.user_id,
            principal.tenant_id,
            repo.MEDICAL_HISTORY_READ_PERMISSION,
        )
    except Exception as exc:  # noqa: BLE001
        logger.warning("Permission resolution failed: %s", exc)
        # Fail-closed for PHI: if we cannot prove the grant, deny.
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Unable to verify medical-history read permission.",
        ) from exc
    if not allowed:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Caller lacks docslot.medical_history.read permission.",
        )


@router.post("/index", response_model=RagIndexResponse)
def index_patient(
    body: RagIndexRequest,
    principal: Principal = Depends(get_principal),
    purpose: str = Depends(require_purpose_of_use),
) -> RagIndexResponse:
    patient_id = _validate_uuid(body.patientId)
    _gate_permission(principal)

    # Cross-tenant / unlinked patient -> 404.
    if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient not found in this tenant.",
        )

    # Consent-or-break-glass gate BEFORE any PHI is read/embedded (403 if denied).
    grant = phi_access.enforce_phi_gate(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
        token_use=principal.token_use,
    )
    # First-class purpose-of-use record (stamps break-glass for the review queue).
    phi_access.record_purpose_of_use(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
        purpose=purpose,
        grant=grant,
    )

    # No history -> 404 (nothing to index).
    if not repo.fetch_medical_history(principal.tenant_id, patient_id):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient has no medical history in this tenant.",
        )

    result = rag.index_patient(principal.tenant_id, patient_id, user_id=principal.user_id)

    repo.write_audit_best_effort(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        action="ai.rag.index",
        purpose=purpose,
    )

    return RagIndexResponse(
        patientId=patient_id,
        recordsIndexed=result["records_indexed"],
        embeddingsTotal=result["embeddings_total"],
        embeddingModel=result["embedding_model"],
        backend=result["backend"],
    )


@router.post("/ask", response_model=RagAskResponse)
def ask(
    body: RagAskRequest,
    principal: Principal = Depends(get_principal),
    purpose: str = Depends(require_purpose_of_use),
) -> RagAskResponse:
    patient_id = _validate_uuid(body.patientId)
    _gate_permission(principal)

    if not repo.patient_linked_to_tenant(principal.tenant_id, patient_id):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient not found in this tenant.",
        )

    # Consent-or-break-glass gate BEFORE any PHI is read (403 if denied).
    grant = phi_access.enforce_phi_gate(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
        token_use=principal.token_use,
    )
    phi_access.record_purpose_of_use(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        resource_type=phi_access.RESOURCE_MEDICAL_HISTORY,
        purpose=purpose,
        grant=grant,
    )

    history = repo.fetch_medical_history(principal.tenant_id, patient_id)
    if not history:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Patient has no medical history in this tenant.",
        )

    # /ask is strictly READ-ONLY: it never auto-indexes (a read MUST NOT persist
    # derived PHI under a read scope — that was a consent-bypass + audit-mislabel
    # leak). If the patient has no embeddings yet, retrieval simply returns nothing;
    # call POST /rag/index (the write path) to build them.
    result = rag.answer(principal.tenant_id, patient_id, body.question, user_id=principal.user_id)

    repo.write_audit_best_effort(
        user_id=principal.user_id,
        tenant_id=principal.tenant_id,
        patient_id=patient_id,
        action="ai.rag.ask",
        purpose=purpose,
    )

    return RagAskResponse(
        patientId=patient_id,
        question=body.question,
        answer=result["answer"],
        mode=result["mode"],
        citations=result["citations"],
        retrieved=result["retrieved"],
    )


@router.get("/status", response_model=RagStatusResponse)
def status_(
    principal: Principal = Depends(get_principal),
) -> RagStatusResponse:
    s = repo.status_for_tenant(principal.tenant_id)
    return RagStatusResponse(
        embeddings=s["embeddings"],
        patientsIndexed=s["patients_indexed"],
        knowledgeBases=[
            KnowledgeBaseInfo(
                kbKey=kb["kb_key"],
                name=kb["name"],
                documentCount=kb["document_count"],
            )
            for kb in s["knowledge_bases"]
        ],
    )
