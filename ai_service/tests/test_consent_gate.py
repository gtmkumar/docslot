"""RAG/OCR consent-or-break-glass gate + bug #3 (/rag/ask is read-only)."""
from __future__ import annotations

import base64
import json
from datetime import datetime, timedelta, timezone

import psycopg
from fastapi.testclient import TestClient

from conftest import (
    KEY_ID,
    PATIENT_CONSENTED,
    PATIENT_NO_CONSENT,
    TENANT_ID,
    USER_ID,
)

INDEX = "/ai/v1/rag/index"
ASK = "/ai/v1/rag/ask"
EXTRACT = "/ai/v1/extractions/lab-report"


def _count(db: psycopg.Connection, table: str, patient_col: str, patient_id: str) -> int:
    with db.cursor() as cur:
        cur.execute(f"SELECT count(*) AS n FROM {table} WHERE {patient_col} = %s", (patient_id,))
        return int(cur.fetchone()["n"])


def _embeddings(db: psycopg.Connection, patient_id: str) -> int:
    return _count(db, "ai.embeddings", "patient_id", patient_id)


def _extractions(db: psycopg.Connection, patient_id: str) -> int:
    return _count(db, "ai.ai_document_extractions", "related_patient_id", patient_id)


# --- No consent + no grant -> 403 on every PHI endpoint, with zero side-effects ---
def test_index_denied_without_consent(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    r = client.post(INDEX, json={"patientId": PATIENT_NO_CONSENT}, headers=auth)
    assert r.status_code == 403, r.text
    assert _embeddings(db, PATIENT_NO_CONSENT) == 0


def test_ask_denied_without_consent(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    r = client.post(ASK, json={"patientId": PATIENT_NO_CONSENT, "question": "any allergies?"}, headers=auth)
    assert r.status_code == 403, r.text
    assert _embeddings(db, PATIENT_NO_CONSENT) == 0


def test_extract_denied_without_consent(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_NO_CONSENT}, headers=auth)
    assert r.status_code == 403, r.text
    assert _extractions(db, PATIENT_NO_CONSENT) == 0


def test_extract_requires_patient_link(client: TestClient, auth: dict) -> None:
    # PHI must be patient-linked: no relatedPatientId -> 422 (never an orphan store).
    r = client.post(EXTRACT, json={}, headers=auth)
    assert r.status_code == 422, r.text


# --- Consented access proceeds and writes ENCRYPTED embeddings + a purpose log ---
def test_index_consented_writes_encrypted(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    r = client.post(INDEX, json={"patientId": PATIENT_CONSENTED}, headers=auth)
    assert r.status_code == 200, r.text
    assert r.json()["recordsIndexed"] >= 1

    with db.cursor() as cur:
        cur.execute(
            """
            SELECT chunk_text, embedding_vector, encryption_key_id
            FROM ai.embeddings WHERE patient_id = %s AND tenant_id = %s
            """,
            (PATIENT_CONSENTED, TENANT_ID),
        )
        rows = cur.fetchall()
    assert rows, "expected encrypted embeddings to be written"
    for row in rows:
        # chunk_text is an encrypted envelope (not the plaintext history).
        assert "Penicillin" not in row["chunk_text"]
        assert "hypertension" not in row["chunk_text"].lower()
        assert str(row["encryption_key_id"]) == KEY_ID
        env = json.loads(base64.b64decode(row["chunk_text"]).decode("utf-8"))
        assert env["keyId"] == KEY_ID
        # embedding_vector holds the base64 envelope (ascii), NOT raw float32 bytes.
        env_v = json.loads(base64.b64decode(bytes(row["embedding_vector"]).decode("ascii")).decode("utf-8"))
        assert set(env_v) == {"keyId", "wrappedKey", "nonce", "ciphertext", "tag"}

    # A first-class, non-break-glass purpose-of-use row was recorded.
    with db.cursor() as cur:
        cur.execute(
            """
            SELECT is_break_glass, review_required FROM platform.purpose_of_use_log
            WHERE tenant_id = %s AND accessed_resource_id = %s
            """,
            (TENANT_ID, PATIENT_CONSENTED),
        )
        log = cur.fetchone()
    assert log is not None
    assert log["is_break_glass"] is False


# --- Bug #3: /rag/ask never auto-indexes (a read MUST NOT persist PHI) ---
def test_ask_is_read_only_no_auto_index(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    assert _embeddings(db, PATIENT_CONSENTED) == 0  # clean slate (no index yet)
    r = client.post(ASK, json={"patientId": PATIENT_CONSENTED, "question": "any allergies?"}, headers=auth)
    assert r.status_code == 200, r.text
    assert r.json()["retrieved"] == 0  # nothing indexed -> nothing retrieved
    # The read created NO embeddings (the bug #3 leak is closed).
    assert _embeddings(db, PATIENT_CONSENTED) == 0


# --- Break-glass: an active grant unlocks a consent-denied read + review-flagged log ---
def _grant_break_glass(db: psycopg.Connection, patient_id: str, resource_type: str) -> None:
    with db.cursor() as cur:
        cur.execute(
            """
            INSERT INTO platform.break_glass_grants
                (user_id, tenant_id, patient_id, resource_type, resource_id, justification, expires_at)
            VALUES (%s, %s, %s, %s, NULL, %s, %s)
            """,
            (
                USER_ID, TENANT_ID, patient_id, resource_type,
                "Unconscious ED patient; need history to treat safely.",
                datetime.now(timezone.utc) + timedelta(hours=1),
            ),
        )


def test_break_glass_unlocks_and_flags_review(client: TestClient, auth: dict, db: psycopg.Connection) -> None:
    _grant_break_glass(db, PATIENT_NO_CONSENT, "medical_history")

    r = client.post(INDEX, json={"patientId": PATIENT_NO_CONSENT}, headers=auth)
    assert r.status_code == 200, r.text  # proceeds despite no consent
    assert _embeddings(db, PATIENT_NO_CONSENT) >= 1

    with db.cursor() as cur:
        cur.execute(
            """
            SELECT is_break_glass, review_required, declared_purpose, break_glass_reason
            FROM platform.purpose_of_use_log
            WHERE tenant_id = %s AND accessed_resource_id = %s
            """,
            (TENANT_ID, PATIENT_NO_CONSENT),
        )
        log = cur.fetchone()
    assert log is not None
    assert log["is_break_glass"] is True
    assert log["review_required"] is True
    assert log["declared_purpose"] == "emergency"
    assert log["break_glass_reason"]


# --- Missing-auth posture is unchanged (401/403 still enforced before the gate) ---
def test_missing_token_is_401(client: TestClient) -> None:
    r = client.post(INDEX, json={"patientId": PATIENT_CONSENTED}, headers={"X-Purpose-Of-Use": "treatment"})
    assert r.status_code == 401
