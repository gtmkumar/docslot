"""A 'service' identity (token_use='service') is DENIED BY DEFAULT — it may reach ONLY the
non-PHI no-show scoring endpoint.

The no-show backfill worker (.NET) mints a short-TTL, non-human SERVICE token to call the AI
without a live caller. The wall is fail-closed at the single auth chokepoint (get_principal):
  - ACCEPTED only on the non-PHI no-show SCORING path (POST /predictions/no-show), and
  - REFUSED (403) on EVERY other authenticated endpoint — every PHI path (rag/index, rag/ask,
    extractions/lab-report, triage, triage run history) AND every ops read (extractions list,
    rag/status, no-show/today),
so a service identity can never reach patient data even though the token is otherwise valid, and
any endpoint added later is denied to it automatically (it must explicitly opt into the allow-list).
"""
from __future__ import annotations

import psycopg
from fastapi.testclient import TestClient

from conftest import PATIENT_CONSENTED, make_service_token

INDEX = "/ai/v1/rag/index"
ASK = "/ai/v1/rag/ask"
RAG_STATUS = "/ai/v1/rag/status"
EXTRACT = "/ai/v1/extractions/lab-report"
EXTRACTIONS_LIST = "/ai/v1/extractions"
TRIAGE = "/ai/v1/triage"
TRIAGE_RUNS = "/ai/v1/triage/runs"
TRIAGE_RUN_DETAIL = "/ai/v1/triage/runs/00000000-0000-0000-0000-000000000000"
NO_SHOW = "/ai/v1/predictions/no-show"
NO_SHOW_TODAY = "/ai/v1/predictions/no-show/today"

_UNKNOWN_BOOKING = "00000000-0000-0000-0000-000000000000"


def _svc_headers() -> dict[str, str]:
    return {"Authorization": f"Bearer {make_service_token()}", "X-Purpose-Of-Use": "treatment"}


def test_service_token_refused_on_rag_ask_even_with_consent(client: TestClient, db: psycopg.Connection) -> None:
    # PATIENT_CONSENTED has active consent, so a USER token would pass — a SERVICE token is refused
    # (403) regardless of consent: get_principal default-denies the service identity at the auth
    # layer, BEFORE any handler / consent / embed / retrieve runs.
    r = client.post(ASK, json={"patientId": PATIENT_CONSENTED, "question": "any allergies?"}, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_rag_index(client: TestClient, db: psycopg.Connection) -> None:
    r = client.post(INDEX, json={"patientId": PATIENT_CONSENTED}, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_rag_status(client: TestClient, db: psycopg.Connection) -> None:
    # Operational (non-PHI) read, but still off-limits to a service identity — no service caller
    # needs it, and default-deny keeps the surface minimal.
    r = client.get(RAG_STATUS, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_extractions(client: TestClient, db: psycopg.Connection) -> None:
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_CONSENTED}, headers=_svc_headers())
    assert r.status_code == 403, r.text
    assert "service identity" in r.text.lower()


def test_service_token_refused_on_extractions_list(client: TestClient, db: psycopg.Connection) -> None:
    r = client.get(EXTRACTIONS_LIST, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_triage_complaint(client: TestClient, db: psycopg.Connection) -> None:
    # Triage runs over the caller-supplied complaint — a service identity has no business creating
    # triage runs, and the run history (below) is PHI. Refused at the auth layer.
    r = client.post(TRIAGE, json={"complaint": "headache for two days"}, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_triage_runs_list(client: TestClient, db: psycopg.Connection) -> None:
    r = client.get(TRIAGE_RUNS, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_triage_run_detail(client: TestClient, db: psycopg.Connection) -> None:
    # The run-detail response carries inputData.complaint (stored free-text symptom PHI). The 403
    # fires at get_principal BEFORE the handler, so the service identity never reaches the read —
    # the run id need not even exist. (Closes the slice-16 auditor HIGH: triage was the one PHI
    # surface the per-endpoint wall had missed.)
    r = client.get(TRIAGE_RUN_DETAIL, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_refused_on_no_show_today(client: TestClient, db: psycopg.Connection) -> None:
    # The whole-day batch read surfaces patient-derived features for every booking; only the
    # single-booking POST scoring path is on the service allow-list, so this is denied.
    r = client.get(NO_SHOW_TODAY, headers=_svc_headers())
    assert r.status_code == 403, r.text


def test_service_token_accepted_on_no_show(client: TestClient, db: psycopg.Connection) -> None:
    # The single-booking scoring path is non-PHI (booking features only) → a service token is
    # accepted via the explicit allow-list (get_principal_allow_service). An unknown booking id
    # yields 404 (NOT 401/403), proving the token authenticated + passed the auth layer.
    r = client.post(NO_SHOW, json={"bookingId": _UNKNOWN_BOOKING}, headers=_svc_headers())
    assert r.status_code == 404, r.text
