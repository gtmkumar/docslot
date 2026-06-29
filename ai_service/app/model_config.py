"""Tenant-aware AI model governance — reads ai.ai_model_configs.

The AI service previously NEVER read ai.ai_model_configs (the schema's source of
truth for which model serves each use_case and whether it may receive PHI), so
flipping allows_phi / rotating a model in the DB had no effect until a full
process restart (bug #10). This module reads it at request time behind a short
TTL cache that picks up DB changes without a restart, and exposes the PHI-egress
gate the RAG answer path enforces BEFORE sending any patient PHI to an EXTERNAL
model.

This is defense-in-depth on top of (and downstream of) the consent gate: the
consent gate decides whether the caller may see the PHI at all; this decides
whether the PHI may be transmitted to a given external model (needs an explicit
allows_phi AND a signed BAA). Local-only paths (sklearn/ONNX embedding, the
extractive answer) never egress PHI and are not gated here.
"""
from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass

from .db import get_connection

logger = logging.getLogger("ai_service.model_config")

# use_case keys (match the schema's documented ai_model_configs.use_case values;
# see database/06_ai_services.sql). Any PHI that egresses to an EXTERNAL model is
# governed per use_case: the RAG answer path and the triage chief-complaint path
# each resolve their own allows_phi-approved model (or stay local).
RAG_USE_CASE = "rag_medical"
TRIAGE_USE_CASE = "triage"
_TTL_SECONDS = 30.0

_lock = threading.Lock()
_cache: dict[tuple[str, str | None], tuple[float, "ModelConfig | None"]] = {}


@dataclass(frozen=True)
class ModelConfig:
    config_id: str
    use_case: str
    provider: str
    model_name: str
    endpoint_url: str | None
    allows_phi: bool
    bao_signed: bool


def _fetch_phi_model_config(use_case: str, tenant_id: str | None) -> ModelConfig | None:
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT config_id, use_case, provider, model_name, endpoint_url, allows_phi, bao_signed
            FROM ai.ai_model_configs
            WHERE use_case = %(uc)s
              AND is_active = true AND allows_phi = true AND bao_signed = true
              AND (tenant_id = %(t)s::uuid OR tenant_id IS NULL)
            ORDER BY (tenant_id IS NOT NULL) DESC, is_default_for_use_case DESC, updated_at DESC
            LIMIT 1
            """,
            {"uc": use_case, "t": tenant_id},
        )
        row = cur.fetchone()
    if row is None:
        return None
    # endpoint_url is operator-controlled DB config used as an httpx base; require an
    # https scheme (defense-in-depth against an SSRF/plaintext egress target). A
    # non-https value is dropped -> the caller falls back to the configured base URL.
    endpoint_url = row["endpoint_url"]
    if endpoint_url is not None and not endpoint_url.lower().startswith("https://"):
        logger.warning(
            "Ignoring non-https endpoint_url %r for %s config %s; using the default base.",
            endpoint_url, row["use_case"], row["config_id"],
        )
        endpoint_url = None
    return ModelConfig(
        config_id=str(row["config_id"]),
        use_case=row["use_case"],
        provider=row["provider"],
        model_name=row["model_name"],
        endpoint_url=endpoint_url,
        allows_phi=bool(row["allows_phi"]),
        bao_signed=bool(row["bao_signed"]),
    )


def get_phi_model_config(use_case: str, tenant_id: str | None) -> ModelConfig | None:
    """The active, PHI-approved (allows_phi AND bao_signed) model for this use_case.

    A tenant-specific config wins over the platform-wide default. Returns None when
    NO model is approved to receive PHI — the caller MUST then keep PHI local (no
    external egress). Cached for _TTL_SECONDS so a DB flip (allows_phi/is_active/
    model rotation; updated_at is trigger-bumped) takes effect without a restart.
    """
    key = (use_case, tenant_id)
    now = time.monotonic()
    with _lock:
        hit = _cache.get(key)
        if hit is not None and now - hit[0] < _TTL_SECONDS:
            return hit[1]
    cfg = _fetch_phi_model_config(use_case, tenant_id)
    with _lock:
        _cache[key] = (now, cfg)
    return cfg


def reset_cache() -> None:
    """Drop the cache (tests, and after a known config change)."""
    with _lock:
        _cache.clear()
