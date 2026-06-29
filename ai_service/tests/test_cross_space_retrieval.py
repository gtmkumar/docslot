"""Bug #12 — retrieval only compares vectors from the SAME embedding space."""
from __future__ import annotations

import numpy as np

from app import rag, rag_repository as repo
from conftest import PATIENT_CONSENTED, TENANT_ID


def _unit_vec(dim: int, seed: int) -> np.ndarray:
    raw = (np.arange(dim, dtype="<f4") + float(seed))
    norm = float(np.linalg.norm(raw)) or 1.0
    return (raw / norm).astype("<f4")


def test_fetch_filters_to_same_model_and_dimension() -> None:
    # One patient, three different vector spaces stored.
    for source_id, model, dim, seed in (
        ("11111111-0000-4000-8000-000000000001", "modelA", 384, 1),
        ("11111111-0000-4000-8000-000000000002", "modelB", 384, 2),
        ("11111111-0000-4000-8000-000000000003", "modelA", 768, 3),
    ):
        repo.insert_embedding(
            tenant_id=TENANT_ID,
            patient_id=PATIENT_CONSENTED,
            source_id=source_id,
            chunk_text=f"{model} dim{dim}",
            chunk_text_hash=f"hash-{model}-{dim}",
            embedding_model=model,
            embedding_dimensions=dim,
            vector=_unit_vec(dim, seed),
            metadata={},
        )

    rows = repo.fetch_patient_embeddings(TENANT_ID, PATIENT_CONSENTED, "modelA", 384)
    assert len(rows) == 1  # NOT the modelB/384 nor the modelA/768 row
    assert rows[0]["embedding_model"] == "modelA"
    assert rows[0]["embedding_dimensions"] == 384
    assert rows[0]["vector"].size == 384


def test_retrieve_ignores_cross_space_and_never_crashes() -> None:
    # Real same-space embeddings (live embedder) for the consented patient.
    res = rag.index_patient(TENANT_ID, PATIENT_CONSENTED)
    assert res["records_indexed"] >= 1

    # A DIFFERENT-DIMENSION rogue row that would crash np.vstack if not filtered out.
    repo.insert_embedding(
        tenant_id=TENANT_ID,
        patient_id=PATIENT_CONSENTED,
        source_id="22222222-0000-4000-8000-000000000001",
        chunk_text="rogue cross-space chunk",
        chunk_text_hash="hash-rogue-768",
        embedding_model="rogue-model",
        embedding_dimensions=768,
        vector=_unit_vec(768, 9),
        metadata={"title": "rogue"},
    )

    hits = rag.retrieve(TENANT_ID, PATIENT_CONSENTED, "any allergies?")  # must NOT raise
    assert hits  # the live-model rows are retrievable
    # The rogue cross-space row never leaks into the results.
    assert all(h["historyId"] != "22222222-0000-4000-8000-000000000001" for h in hits)
