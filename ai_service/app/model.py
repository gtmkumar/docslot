"""No-show probability model.

Two paths, both deterministic and fully offline (no external LLM/API):

1. ``v1-logreg`` — a scikit-learn LogisticRegression trained on the tenant's
   historical labeled bookings, used only when there are enough samples AND both
   classes are present.
2. ``v1-heuristic`` — a transparent, calibrated heuristic fallback for sparse
   tenants (the demo tenant has ~10 bookings / 1 no-show). It produces a sane
   [0, 1] probability from base rate + explainable weighted bumps.

Trained models are cached per tenant and reused (lazy training).
"""
from __future__ import annotations

import logging
import threading
from dataclasses import dataclass

import numpy as np
from sklearn.linear_model import LogisticRegression

from .config import get_settings
from .features import build_features, vectorize
from .repository import label_of

logger = logging.getLogger("ai_service.model")

# Population base rate fallback when a tenant has no usable history at all.
_DEFAULT_BASE_RATE = 0.15


@dataclass
class ScoreResult:
    probability: float
    model_version: str
    confidence_interval: dict | None


@dataclass
class _TrainedModel:
    clf: LogisticRegression
    base_rate: float


# Per-tenant cache of trained classifiers. `None` => heuristic path chosen.
_cache: dict[str, _TrainedModel | None] = {}
_cache_base_rate: dict[str, float] = {}
_lock = threading.Lock()


def _train_for_tenant(tenant_id: str) -> tuple[_TrainedModel | None, float]:
    """Attempt to train a logreg model. Returns (model_or_None, base_rate)."""
    settings = get_settings()

    labels: list[int] = []
    vectors: list[list[float]] = []

    # Labeled training rows carry their status alongside the feature input so we
    # can derive the 1/0 label. (BookingFeatureInput omits status by design.)
    for feats_input, status in _fetch_labeled(tenant_id):
        lbl = label_of(status)
        if lbl is None:
            continue
        labels.append(lbl)
        vectors.append(vectorize(build_features(feats_input)))

    n = len(labels)
    base_rate = (sum(labels) / n) if n > 0 else _DEFAULT_BASE_RATE

    if n < settings.min_training_samples or len(set(labels)) < 2:
        logger.info(
            "Tenant %s: %d labeled samples, classes=%s -> heuristic fallback.",
            tenant_id, n, set(labels),
        )
        return None, base_rate

    X = np.asarray(vectors, dtype=float)
    y = np.asarray(labels, dtype=int)
    clf = LogisticRegression(max_iter=1000, class_weight="balanced")
    clf.fit(X, y)
    logger.info("Tenant %s: trained logreg on %d samples.", tenant_id, n)
    return _TrainedModel(clf=clf, base_rate=base_rate), base_rate


def _fetch_labeled(tenant_id: str):
    """Return [(BookingFeatureInput, status), ...] for labeled training rows.

    Implemented here (rather than in repository) because it needs the raw status
    alongside the feature input; reuses the repository's tenant-scoped base query.
    """
    from .db import get_connection
    from .repository import _BASE_SELECT, _to_feature_input, _NO_SHOW, _KEPT

    statuses = list(_NO_SHOW + _KEPT)
    out = []
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            _BASE_SELECT
            + " WHERE b.tenant_id = %(tenant_id)s AND b.status = ANY(%(statuses)s)",
            {"tenant_id": tenant_id, "statuses": statuses},
        )
        for row in cur.fetchall():
            out.append((_to_feature_input(row), row["status"]))
    return out


def _get_model(tenant_id: str) -> tuple[_TrainedModel | None, float]:
    with _lock:
        if tenant_id not in _cache:
            model, base_rate = _train_for_tenant(tenant_id)
            _cache[tenant_id] = model
            _cache_base_rate[tenant_id] = base_rate
        return _cache[tenant_id], _cache_base_rate[tenant_id]


def _heuristic(feats: dict, base_rate: float) -> float:
    """Transparent, calibrated heuristic. Starts at the tenant base rate and
    applies bounded, explainable bumps. Result clamped to [0.02, 0.95]."""
    p = base_rate

    # Channel risk: walk_ins and phone bookings no-show more; whatsapp confirmed less.
    channel = feats["booked_via"]
    if channel == "walk_in":
        p += 0.20
    elif channel == "phone_call":
        p += 0.10
    elif channel == "whatsapp":
        p -= 0.03

    # New patient with no history is riskier.
    if feats["is_new_patient"]:
        p += 0.10
    else:
        # Patient's own prior no-show rate is a strong signal.
        p += 0.40 * feats["prior_no_show_rate"]

    # Long lead time -> more likely to forget.
    if feats["lead_time_hours"] > 168:      # > 1 week
        p += 0.10
    elif feats["lead_time_hours"] > 72:     # > 3 days
        p += 0.05

    # Early-morning slots are missed more often.
    if feats["is_early_morning"]:
        p += 0.05

    # A note/chief-complaint signals engagement -> slightly lower risk.
    if feats["has_notes"]:
        p -= 0.03

    return float(min(0.95, max(0.02, p)))


def score(tenant_id: str, feats: dict) -> ScoreResult:
    """Return a no-show probability for one feature dict (tenant-scoped model)."""
    model, base_rate = _get_model(tenant_id)

    if model is None:
        prob = _heuristic(feats, base_rate)
        return ScoreResult(
            probability=round(prob, 4),
            model_version="v1-heuristic",
            confidence_interval={"method": "heuristic", "base_rate": round(base_rate, 4)},
        )

    vec = np.asarray([vectorize(feats)], dtype=float)
    prob = float(model.clf.predict_proba(vec)[0, 1])
    return ScoreResult(
        probability=round(prob, 4),
        model_version="v1-logreg",
        confidence_interval={"method": "logreg", "base_rate": round(base_rate, 4)},
    )


def risk_band(prob: float) -> str:
    if prob < 0.34:
        return "low"
    if prob < 0.67:
        return "medium"
    return "high"


def reset_cache() -> None:
    """Test/ops helper to drop cached models."""
    with _lock:
        _cache.clear()
        _cache_base_rate.clear()
