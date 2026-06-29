"""Bug #10 — RAG reads ai.ai_model_configs and enforces the allows_phi egress gate."""
from __future__ import annotations

from types import SimpleNamespace

import psycopg

from app import model_config, triage
from app.model_config import RAG_USE_CASE, get_phi_model_config
from conftest import TENANT_ID


def _set_tenant_config(
    db: psycopg.Connection, *, allows_phi: bool, bao_signed: bool, is_active: bool,
    model_name: str = "tenant-claude",
) -> None:
    with db.cursor() as cur:
        cur.execute(
            "DELETE FROM ai.ai_model_configs WHERE tenant_id = %s AND use_case = %s",
            (TENANT_ID, RAG_USE_CASE),
        )
        cur.execute(
            """
            INSERT INTO ai.ai_model_configs
                (tenant_id, use_case, provider, model_name, endpoint_url,
                 allows_phi, bao_signed, is_active)
            VALUES (%s, %s, 'anthropic', %s, 'https://api.anthropic.com', %s, %s, %s)
            """,
            (TENANT_ID, RAG_USE_CASE, model_name, allows_phi, bao_signed, is_active),
        )


def test_platform_default_disallows_phi() -> None:
    # Only the seeded platform-wide default (allows_phi=false) applies to this tenant,
    # so no model is approved to receive PHI -> egress stays off (extractive only).
    assert get_phi_model_config(RAG_USE_CASE, TENANT_ID) is None


def test_phi_requires_allows_and_bao_and_active(db: psycopg.Connection) -> None:
    cases = [
        (False, True, True, False),   # not allowed
        (True, False, True, False),   # no signed BAA
        (True, True, False, False),   # inactive
        (True, True, True, True),     # approved
    ]
    for allows_phi, bao_signed, is_active, should_resolve in cases:
        _set_tenant_config(db, allows_phi=allows_phi, bao_signed=bao_signed, is_active=is_active)
        model_config.reset_cache()
        cfg = get_phi_model_config(RAG_USE_CASE, TENANT_ID)
        if should_resolve:
            assert cfg is not None and cfg.allows_phi and cfg.bao_signed
            assert cfg.model_name == "tenant-claude"  # tenant config wins over the default
        else:
            assert cfg is None, (allows_phi, bao_signed, is_active)


def test_cache_invalidates_without_restart(db: psycopg.Connection) -> None:
    _set_tenant_config(db, allows_phi=False, bao_signed=False, is_active=True)
    model_config.reset_cache()
    assert get_phi_model_config(RAG_USE_CASE, TENANT_ID) is None  # cached negative

    # Operator records a signed BAA and approves the model for PHI (updated_at bumped).
    with db.cursor() as cur:
        cur.execute(
            "UPDATE ai.ai_model_configs SET allows_phi = true, bao_signed = true "
            "WHERE tenant_id = %s AND use_case = %s",
            (TENANT_ID, RAG_USE_CASE),
        )
    # Still the cached negative within the TTL window...
    assert get_phi_model_config(RAG_USE_CASE, TENANT_ID) is None
    # ...until the cache is invalidated — no process restart needed (bug #10).
    model_config.reset_cache()
    cfg = get_phi_model_config(RAG_USE_CASE, TENANT_ID)
    assert cfg is not None and cfg.allows_phi and cfg.bao_signed


def test_triage_complaint_not_egressed_without_approved_model(monkeypatch) -> None:
    # The triage path egresses the patient's free-text complaint to an external model;
    # it must be governed by the SAME allows_phi gate as RAG. Force a configured API
    # key so we exercise the gate (not the no-key short-circuit), then assert that with
    # only the allows_phi=false 'triage' default the complaint is NOT sent out.
    monkeypatch.setattr(
        triage, "get_settings",
        lambda: SimpleNamespace(llm_api_key="test-key", openai_api_key=None),
    )
    model_config.reset_cache()
    assert triage._extract_llm("severe chest pain radiating to the left arm", TENANT_ID) is None
