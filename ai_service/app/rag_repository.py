"""Data access for the RAG-over-patient-medical-history workflow.

PHI ISOLATION RULES (this service uses the owner connection, which BYPASSES RLS):
  - `ai.embeddings` has NO row-level security at all, so tenant + patient scoping
    is THIS code's responsibility. EVERY read of embeddings/history filters
    `tenant_id = <jwt tenant>` AND `patient_id = <requested patient>`.
  - A patient is only "in" a tenant if a row exists in
    `docslot.patient_tenant_links` for (patient_id, tenant_id). We gate on that
    link, NOT just on the medical-history rows.
"""
from __future__ import annotations

import json
import logging

import numpy as np

from .db import get_connection
from .encryption import EncryptionContext, FieldEncryptor

logger = logging.getLogger("ai_service.rag_repository")

MEDICAL_HISTORY_READ_PERMISSION = "docslot.medical_history.read"
# PHI written by the RAG path carries the 'medical_history' data_class (it is
# derived from patient_medical_history); the encryptor resolves the tenant's key.
_RESOURCE_TYPE = "medical_history"


def _enc_ctx(tenant_id: str, patient_id: str, user_id: str | None) -> EncryptionContext:
    return EncryptionContext(
        user_id=user_id,
        tenant_id=tenant_id,
        resource_type=_RESOURCE_TYPE,
        resource_id=patient_id,
    )


# ---------------------------------------------------------------------------
# Permission resolution (best-effort PHI gate)
# ---------------------------------------------------------------------------
def has_permission(user_id: str, tenant_id: str, permission_key: str) -> bool:
    """Resolve the caller's effective permission set via the canonical
    `platform.resolve_user_permissions(user, tenant)` and check membership.

    Returns False on any DB error (fail-closed for the PHI gate is the caller's
    choice; here we surface the boolean and let the router decide its posture).
    """
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT 1
            FROM platform.resolve_user_permissions(%(user_id)s, %(tenant_id)s)
            WHERE permission_key = %(perm)s
            LIMIT 1
            """,
            {"user_id": user_id, "tenant_id": tenant_id, "perm": permission_key},
        )
        return cur.fetchone() is not None


# ---------------------------------------------------------------------------
# Patient / medical history (tenant + patient scoped)
# ---------------------------------------------------------------------------
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


def fetch_medical_history(tenant_id: str, patient_id: str) -> list[dict]:
    """Tenant + patient scoped active medical-history rows for indexing."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT history_id, patient_id, record_type, title, description,
                   severity, icd10_code, is_active, is_critical, started_date
            FROM docslot.patient_medical_history
            WHERE tenant_id = %(tenant_id)s
              AND patient_id = %(patient_id)s
            ORDER BY is_critical DESC, added_at
            """,
            {"tenant_id": tenant_id, "patient_id": patient_id},
        )
        return list(cur.fetchall())


def build_chunk_text(row: dict) -> str:
    """Render one history row into the text we embed.

    Form: "[record_type] title — description (severity: …, ICD: …)".
    """
    parts = f"[{row['record_type']}] {row['title']}"
    if row.get("description"):
        parts += f" — {row['description']}"
    tail: list[str] = []
    if row.get("severity"):
        tail.append(f"severity: {row['severity']}")
    if row.get("icd10_code"):
        tail.append(f"ICD: {row['icd10_code']}")
    if row.get("is_critical"):
        tail.append("CRITICAL")
    if tail:
        parts += f" ({', '.join(tail)})"
    return parts


# ---------------------------------------------------------------------------
# Embeddings (NO RLS — tenant + patient scoping is mandatory here)
# ---------------------------------------------------------------------------
def embedding_exists(tenant_id: str, source_id: str, chunk_text_hash: str) -> bool:
    """Idempotency probe: a row with this tenant + source + hash already stored."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT 1 FROM ai.embeddings
            WHERE tenant_id = %(tenant_id)s
              AND source_type = 'patient_medical_history'
              AND source_id = %(source_id)s
              AND chunk_text_hash = %(hash)s
              AND deleted_at IS NULL
            LIMIT 1
            """,
            {"tenant_id": tenant_id, "source_id": source_id, "hash": chunk_text_hash},
        )
        return cur.fetchone() is not None


def insert_embedding(
    *,
    tenant_id: str,
    patient_id: str,
    source_id: str,
    chunk_text: str,
    chunk_text_hash: str,
    embedding_model: str,
    embedding_dimensions: int,
    vector: np.ndarray,
    metadata: dict,
    user_id: str | None = None,
) -> None:
    """Insert one ai.embeddings row with chunk_text + vector ENCRYPTED at rest.

    chunk_text and the float32 vector are PHI (medical_history data_class); both
    are envelope-encrypted (see ai_service/app/encryption.py) and encryption_key_id
    is set. The metadata 'title' (an encrypted-class field) is stored as an
    encrypted 'title_enc' envelope; record_type/severity/icd10/is_critical are
    non-encrypted scalars and stay plaintext. chunk_text_hash remains a hash of the
    PLAINTEXT chunk for idempotent dedup. Encryption + the forensic key_usage_log
    share this row's transaction, so a rolled-back insert also rolls back its log.
    """
    raw_vec = vector.astype("<f4").tobytes()
    meta = dict(metadata)
    title = meta.pop("title", None)
    with get_connection() as conn:
        enc = FieldEncryptor(conn, _enc_ctx(tenant_id, patient_id, user_id))
        chunk_payload, key_id = enc.encrypt_text(chunk_text)
        vec_payload, _ = enc.encrypt_blob(raw_vec)
        if title:
            meta["title_enc"], _ = enc.encrypt_text(title)
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO ai.embeddings (
                    tenant_id, source_type, source_id, chunk_index, chunk_text,
                    chunk_text_hash, embedding_model, embedding_dimensions,
                    embedding_vector, metadata, patient_id, encryption_key_id
                ) VALUES (
                    %(tenant_id)s, 'patient_medical_history', %(source_id)s, 0, %(chunk_text)s,
                    %(hash)s, %(model)s, %(dims)s,
                    %(vector)s, %(metadata)s, %(patient_id)s, %(key_id)s
                )
                """,
                {
                    "tenant_id": tenant_id,
                    "source_id": source_id,
                    "chunk_text": chunk_payload,
                    "hash": chunk_text_hash,
                    "model": embedding_model,
                    "dims": embedding_dimensions,
                    "vector": vec_payload.encode("ascii"),
                    "metadata": json.dumps(meta),
                    "patient_id": patient_id,
                    "key_id": key_id,
                },
            )


def fetch_patient_embeddings(
    tenant_id: str, patient_id: str, user_id: str | None = None
) -> list[dict]:
    """Load this patient's stored embeddings (tenant + patient scoped).

    The encrypted float32 vector is decrypted to a numpy array under 'vector'
    (needed to rank). chunk_text stays as its ENCRYPTED payload under
    'chunk_text_enc' and is decrypted LAZILY for only the top-k hits actually
    returned (see decrypt_payloads), so PHI that is never disclosed is never
    decrypted.
    """
    with get_connection() as conn:
        enc = FieldEncryptor(conn, _enc_ctx(tenant_id, patient_id, user_id))
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT embedding_id, source_id, chunk_text, chunk_text_hash,
                       embedding_model, embedding_dimensions, embedding_vector, metadata
                FROM ai.embeddings
                WHERE tenant_id = %(tenant_id)s
                  AND patient_id = %(patient_id)s
                  AND source_type = 'patient_medical_history'
                  AND deleted_at IS NULL
                """,
                {"tenant_id": tenant_id, "patient_id": patient_id},
            )
            rows = list(cur.fetchall())
        out: list[dict] = []
        for r in rows:
            raw = r["embedding_vector"]
            if raw is not None:
                payload = bytes(raw).decode("ascii")
                r["vector"] = np.frombuffer(enc.decrypt_blob(payload), dtype="<f4")
            else:
                r["vector"] = None
            r["chunk_text_enc"] = r.pop("chunk_text")
            out.append(r)
    return out


def decrypt_payloads(
    payloads: list[str | None], *, tenant_id: str, patient_id: str, user_id: str | None = None
) -> list[str | None]:
    """Decrypt a list of envelope payloads (None passes through), in one tx.

    Used to decrypt the top-k chunk_text + title PHI actually surfaced to the
    caller — each decrypt is logged to platform.key_usage_log.
    """
    if not payloads:
        return []
    with get_connection() as conn:
        enc = FieldEncryptor(conn, _enc_ctx(tenant_id, patient_id, user_id))
        return [enc.decrypt_text(p) if p else None for p in payloads]


# ---------------------------------------------------------------------------
# Knowledge base registry
# ---------------------------------------------------------------------------
def upsert_knowledge_base(
    *,
    tenant_id: str,
    kb_key: str,
    name: str,
    embedding_model: str,
    document_count: int,
) -> None:
    """Create or update the ai.ai_knowledge_bases row for this tenant + kb_key."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO ai.ai_knowledge_bases (
                kb_key, tenant_id, name, source_type, embedding_model,
                document_count, last_indexed_at, requires_consent,
                permission_required, is_active
            ) VALUES (
                %(kb_key)s, %(tenant_id)s, %(name)s, 'patient_records', %(model)s,
                %(doc_count)s, NOW(), true,
                %(perm)s, true
            )
            ON CONFLICT (kb_key, tenant_id) DO UPDATE SET
                name = EXCLUDED.name,
                embedding_model = EXCLUDED.embedding_model,
                document_count = EXCLUDED.document_count,
                last_indexed_at = NOW(),
                is_active = true
            """,
            {
                "kb_key": kb_key,
                "tenant_id": tenant_id,
                "name": name,
                "model": embedding_model,
                "doc_count": document_count,
                "perm": MEDICAL_HISTORY_READ_PERMISSION,
            },
        )


# ---------------------------------------------------------------------------
# Status (tenant scoped)
# ---------------------------------------------------------------------------
def status_for_tenant(tenant_id: str) -> dict:
    """Counts + KB registry for the tenant's patient_medical_history embeddings."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT count(*) AS embeddings,
                   count(DISTINCT patient_id) AS patients_indexed
            FROM ai.embeddings
            WHERE tenant_id = %(tenant_id)s
              AND source_type = 'patient_medical_history'
              AND deleted_at IS NULL
            """,
            {"tenant_id": tenant_id},
        )
        counts = cur.fetchone()

        cur.execute(
            """
            SELECT kb_key, name, document_count
            FROM ai.ai_knowledge_bases
            WHERE tenant_id = %(tenant_id)s AND is_active = true
            ORDER BY kb_key
            """,
            {"tenant_id": tenant_id},
        )
        kbs = list(cur.fetchall())

    return {
        "embeddings": int(counts["embeddings"]),
        "patients_indexed": int(counts["patients_indexed"]),
        "knowledge_bases": kbs,
    }


# ---------------------------------------------------------------------------
# Compliance logging
# ---------------------------------------------------------------------------
# NOTE: purpose-of-use logging moved to app/phi_access.record_purpose_of_use,
# which is a FIRST-CLASS (non-swallowed) write that stamps break-glass accesses
# for the security review queue. The hash-chained audit_log write below stays
# best-effort (supplementary to the purpose-of-use record).
def write_audit_best_effort(
    *,
    user_id: str,
    tenant_id: str,
    patient_id: str,
    action: str,
    purpose: str,
) -> None:
    """Best-effort hash-chained audit write for a PHI access. Never blocks."""
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.audit_log (
                    user_id, tenant_id, action, resource_type, resource_id,
                    purpose, legal_basis, success
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, %(action)s, 'medical_history', %(resource_id)s,
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
