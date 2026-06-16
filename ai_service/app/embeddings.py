"""Pluggable text embedder for the RAG workflow.

Two backends, both producing a deterministic fixed-dimension float32 vector:

1. ``fastembed`` (PREFERRED) — a real semantic ONNX model
   (``BAAI/bge-small-en-v1.5``, 384-dim, L2-normalized). Downloaded on first use.
2. ``hashing`` (FALLBACK) — a fully-offline scikit-learn ``HashingVectorizer``
   (n_features=384, alternate_sign=False) + L2 normalization. No download, no
   network; a fixed, reusable lexical embedding space. Used only when fastembed
   cannot initialize (e.g. offline / model download fails).

Both share the SAME dimension so a knowledge base indexed with one backend can be
queried with that same backend consistently. The active backend is reported back
to callers via ``Embedder.backend``.

The embedder is process-cached (one instance, lazily built).
"""
from __future__ import annotations

import logging
import threading
from typing import Sequence

import numpy as np

logger = logging.getLogger("ai_service.embeddings")

# Fixed dimension shared by both backends. bge-small-en-v1.5 is natively 384-dim.
EMBED_DIM = 384
_FASTEMBED_MODEL = "BAAI/bge-small-en-v1.5"


def _l2_normalize(mat: np.ndarray) -> np.ndarray:
    """Row-wise L2 normalize a (n, d) float32 matrix. Zero rows stay zero."""
    norms = np.linalg.norm(mat, axis=1, keepdims=True)
    norms[norms == 0.0] = 1.0
    return (mat / norms).astype("<f4")


class Embedder:
    """A fixed-dimension text embedder with a labelled backend.

    ``backend`` is one of ``'fastembed'`` or ``'hashing'``.
    ``model_label`` is a stable string persisted to ai.embeddings.embedding_model.
    """

    def __init__(self) -> None:
        self.dim = EMBED_DIM
        self._fastembed = None
        self._hashing = None
        self._tfidf_normalizer = None

        # Try the real semantic backend first.
        try:
            from fastembed import TextEmbedding  # type: ignore

            self._fastembed = TextEmbedding(_FASTEMBED_MODEL)
            # Force a real embed so a deferred download failure surfaces NOW,
            # not on the first user request.
            probe = next(iter(self._fastembed.embed(["warmup"])))
            if len(probe) != EMBED_DIM:
                raise RuntimeError(
                    f"fastembed dim {len(probe)} != expected {EMBED_DIM}"
                )
            self.backend = "fastembed"
            self.model_label = _FASTEMBED_MODEL
            logger.info("Embedder backend=fastembed model=%s dim=%d", _FASTEMBED_MODEL, EMBED_DIM)
            return
        except Exception as exc:  # noqa: BLE001 — any failure -> offline fallback
            logger.warning(
                "fastembed unavailable (%s); falling back to offline HashingVectorizer.",
                exc,
            )

        # Fully-offline fallback: lexical hashing into a fixed space.
        from sklearn.feature_extraction.text import HashingVectorizer

        self._hashing = HashingVectorizer(
            n_features=EMBED_DIM,
            alternate_sign=False,
            norm=None,  # we L2-normalize ourselves for cosine consistency
        )
        self.backend = "hashing"
        self.model_label = f"hashing-vectorizer-{EMBED_DIM}"
        logger.info("Embedder backend=hashing model=%s dim=%d", self.model_label, EMBED_DIM)

    def embed(self, texts: Sequence[str]) -> np.ndarray:
        """Embed a list of texts -> (n, EMBED_DIM) float32, L2-normalized."""
        if not texts:
            return np.zeros((0, self.dim), dtype="<f4")

        if self.backend == "fastembed":
            vecs = np.asarray(list(self._fastembed.embed(list(texts))), dtype="<f4")
            # bge output is already normalized, but re-normalize for safety.
            return _l2_normalize(vecs)

        # Hashing backend.
        mat = self._hashing.transform(list(texts)).toarray().astype("<f4")
        return _l2_normalize(mat)

    def embed_one(self, text: str) -> np.ndarray:
        return self.embed([text])[0]


_instance: Embedder | None = None
_lock = threading.Lock()


def get_embedder() -> Embedder:
    """Process-cached embedder. Built lazily on first use."""
    global _instance
    if _instance is None:
        with _lock:
            if _instance is None:
                _instance = Embedder()
    return _instance
