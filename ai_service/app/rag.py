"""RAG core: indexing, cosine retrieval, and extractive/LLM answer synthesis.

All retrieval similarity is computed APP-SIDE in numpy because ai.embeddings
stores vectors as bytea (not pgvector). Vectors are L2-normalized at embed time,
so cosine similarity reduces to a dot product.
"""
from __future__ import annotations

import hashlib
import logging

import numpy as np

from . import rag_repository as repo
from .config import get_settings
from .embeddings import get_embedder
from .model_config import RAG_USE_CASE, get_phi_model_config

logger = logging.getLogger("ai_service.rag")

KB_KEY = "patient_medical_history"
KB_NAME = "Patient Medical History"
TOP_K = 4


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# Indexing
# ---------------------------------------------------------------------------
def index_patient(tenant_id: str, patient_id: str, user_id: str | None = None) -> dict:
    """Embed + upsert this patient's medical-history rows (idempotent).

    Caller MUST have already verified the patient is linked to the tenant, has
    history, and passed the consent/break-glass gate. Embeddings are written
    ENCRYPTED at rest. Returns indexing stats including the active embedding backend.
    """
    embedder = get_embedder()
    rows = repo.fetch_medical_history(tenant_id, patient_id)

    chunks: list[tuple[dict, str, str]] = []  # (row, chunk_text, hash)
    for row in rows:
        text = repo.build_chunk_text(row)
        chunks.append((row, text, _sha256(text)))

    # Idempotent: skip chunks already stored (same tenant + source + hash).
    to_embed = [
        (row, text, h)
        for (row, text, h) in chunks
        if not repo.embedding_exists(tenant_id, str(row["history_id"]), h)
    ]

    indexed = 0
    if to_embed:
        vectors = embedder.embed([t for (_, t, _) in to_embed])
        for (row, text, h), vec in zip(to_embed, vectors):
            repo.insert_embedding(
                tenant_id=tenant_id,
                patient_id=patient_id,
                source_id=str(row["history_id"]),
                chunk_text=text,
                chunk_text_hash=h,
                embedding_model=embedder.model_label,
                embedding_dimensions=embedder.dim,
                vector=vec,
                metadata={
                    "record_type": row["record_type"],
                    "title": row["title"],
                    "severity": row["severity"],
                    "icd10_code": row["icd10_code"],
                    "is_critical": bool(row["is_critical"]),
                },
                user_id=user_id,
            )
            indexed += 1

    # Tenant-wide document count for the KB registry row.
    total = repo.status_for_tenant(tenant_id)["embeddings"]
    repo.upsert_knowledge_base(
        tenant_id=tenant_id,
        kb_key=KB_KEY,
        name=KB_NAME,
        embedding_model=embedder.model_label,
        document_count=total,
    )

    return {
        "records_indexed": indexed,
        "embeddings_total": total,
        "embedding_model": embedder.model_label,
        "backend": embedder.backend,
    }


# ---------------------------------------------------------------------------
# Retrieval
# ---------------------------------------------------------------------------
def retrieve(
    tenant_id: str, patient_id: str, question: str, user_id: str | None = None, k: int = TOP_K
) -> list[dict]:
    """Cosine top-k over this patient's stored embeddings (tenant + patient scoped).

    Vectors are decrypted to rank; the chunk_text + title PHI of ONLY the top-k
    hits actually surfaced is then decrypted (each decrypt logged to key_usage_log).
    """
    embedder = get_embedder()
    qvec = embedder.embed_one(question)  # already L2-normalized
    # Same vector space only (bug #12): a query embedded with this model/dim is only
    # comparable to rows embedded with the SAME model/dim.
    stored = repo.fetch_patient_embeddings(
        tenant_id, patient_id, embedder.model_label, embedder.dim, user_id=user_id
    )
    stored = [s for s in stored if s["vector"] is not None and s["vector"].size == qvec.size]
    if not stored:
        return []

    matrix = np.vstack([s["vector"] for s in stored]).astype("<f4")
    # Vectors are L2-normalized -> cosine == dot product.
    scores = matrix @ qvec

    order = [int(i) for i in np.argsort(-scores)[:k]]
    top = [stored[i] for i in order]
    # Decrypt only the disclosed top-k PHI (chunk_text + title) in one transaction.
    chunk_texts = repo.decrypt_payloads(
        [s["chunk_text_enc"] for s in top], tenant_id=tenant_id, patient_id=patient_id, user_id=user_id
    )
    titles = repo.decrypt_payloads(
        [(s["metadata"] or {}).get("title_enc") for s in top],
        tenant_id=tenant_id, patient_id=patient_id, user_id=user_id,
    )

    results = []
    for s, idx, chunk_text, title in zip(top, order, chunk_texts, titles):
        meta = s["metadata"] or {}
        results.append(
            {
                "historyId": str(s["source_id"]),
                "recordType": meta.get("record_type"),
                "title": title,
                "severity": meta.get("severity"),
                "score": round(float(scores[idx]), 4),
                "chunkText": chunk_text,
            }
        )
    return results


# ---------------------------------------------------------------------------
# Answer synthesis
# ---------------------------------------------------------------------------
# Below this cosine score, even the best hit is treated as weakly relevant.
# bge-small cosine for a clear on-topic match runs ~0.6+; off-topic ~0.4 or less.
_RELEVANCE_FLOOR = 0.45


def _extractive_answer(question: str, hits: list[dict]) -> str:
    """Stitch a concise extractive answer from the rank-ordered top chunks.

    When the strongest hit is below the relevance floor, lead with an explicit
    "no clearly relevant record" note (e.g. asking about surgeries for a patient
    with none) before listing the closest records for context.
    """
    if not hits:
        return "No relevant medical history was found for this patient."

    top_score = hits[0]["score"]
    lines: list[str] = []
    if top_score < _RELEVANCE_FLOOR:
        lines.append(
            "No clearly relevant record was found for this question in the "
            "patient's medical history. The closest (low-confidence) records are:"
        )
    else:
        lines.append(
            "Based on this patient's medical history, the most relevant records "
            f'for "{question}" are:'
        )
    for i, h in enumerate(hits, start=1):
        sev = f" [{h['severity']}]" if h.get("severity") else ""
        lines.append(f"{i}. {h['chunkText']}{sev}")
    return "\n".join(lines)


def _llm_answer(question: str, hits: list[dict], tenant_id: str) -> str | None:
    """Optional LLM synthesis over retrieved chunks. None on any failure/no-key.

    Strictly grounded in the retrieved chunks (no outside knowledge) to keep PHI
    answers faithful. PHI-egress is GOVERNED (bug #10): the chunks are patient PHI,
    so this only calls an EXTERNAL model that ai.ai_model_configs explicitly
    approves for PHI (allows_phi AND a signed BAA), read at request time (TTL
    cached) so a revoked approval / rotated model takes effect without a restart.
    With no approved model we return None -> the caller falls back to the LOCAL
    extractive answer, so PHI never leaves the boundary.
    """
    settings = get_settings()
    api_key = settings.llm_api_key or settings.openai_api_key
    if not api_key:
        return None

    cfg = get_phi_model_config(RAG_USE_CASE, tenant_id)
    if cfg is None:
        logger.info(
            "No PHI-approved rag_medical model for tenant %s; keeping PHI local "
            "(extractive answer, no external egress).",
            tenant_id,
        )
        return None

    context = "\n".join(f"- {h['chunkText']}" for h in hits)
    system = (
        "You are a clinical assistant. Answer ONLY from the provided patient "
        "medical-history records. If the records do not contain the answer, say "
        "so plainly. Be concise. Do not invent facts."
    )
    user = f"Patient records:\n{context}\n\nQuestion: {question}"

    try:
        import httpx

        # Model + endpoint come from the DB-approved config (not stale process state).
        base = (cfg.endpoint_url or settings.llm_base_url).rstrip("/")
        # Anthropic Messages API shape (default). Override base/model for others.
        resp = httpx.post(
            f"{base}/v1/messages",
            headers={
                "x-api-key": api_key,
                "anthropic-version": "2023-06-01",
                "content-type": "application/json",
            },
            json={
                "model": cfg.model_name,
                "max_tokens": 400,
                "system": system,
                "messages": [{"role": "user", "content": user}],
            },
            timeout=30.0,
        )
        resp.raise_for_status()
        data = resp.json()
        # Anthropic returns {"content":[{"type":"text","text":...}]}
        blocks = data.get("content", [])
        text = "".join(b.get("text", "") for b in blocks if b.get("type") == "text")
        return text.strip() or None
    except Exception as exc:  # noqa: BLE001 — fall back to extractive
        logger.warning("LLM synthesis failed; using extractive answer: %s", exc)
        return None


def answer(tenant_id: str, patient_id: str, question: str, user_id: str | None = None) -> dict:
    """Retrieve + synthesize. Returns answer, mode, citations, retrieved count."""
    hits = retrieve(tenant_id, patient_id, question, user_id=user_id)

    mode = "extractive"
    text = None
    if hits:
        text = _llm_answer(question, hits, tenant_id)
        if text is not None:
            mode = "llm"
    if text is None:
        text = _extractive_answer(question, hits)

    citations = [
        {
            "historyId": h["historyId"],
            "recordType": h["recordType"],
            "title": h["title"],
            "severity": h["severity"],
            "score": h["score"],
        }
        for h in hits
    ]
    return {
        "answer": text,
        "mode": mode,
        "citations": citations,
        "retrieved": len(hits),
    }
