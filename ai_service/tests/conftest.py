"""Integration-test fixtures for the AI PHI-at-rest slice.

Runs against the live ``docslot_platform`` dev DB (the AI service connects as the
owner role; see ai_service/app/db.py). Seeds a dedicated, FIXED test tenant /
user / patients / medical history / encryption key once per session (idempotent
upserts, so re-running accumulates nothing), and clears the per-test PHI leaf
rows before every test. The tenant/user/patients persist across runs so the
incidental audit_log rows they reference never dangle (audit_log is owner-DELETE
blocked by design).
"""
from __future__ import annotations

import base64
import time
from collections.abc import Iterator
from datetime import datetime, timezone

import jwt
import psycopg
import pytest
from fastapi.testclient import TestClient
from psycopg.rows import dict_row

from app import model_config
from app.config import get_settings
from app.main import app

# --- Fixed identifiers (valid hex UUIDs, recognizable prefix) ---------------
TENANT_ID = "aaaaaaaa-0000-4000-8000-000000000001"
USER_ID = "aaaaaaaa-0000-4000-8000-0000000000d0"
PATIENT_CONSENTED = "aaaaaaaa-0000-4000-8000-00000000c0de"
PATIENT_NO_CONSENT = "aaaaaaaa-0000-4000-8000-00000000dead"
KEY_ID = "aaaaaaaa-0000-7000-8000-000000000001"
# 32-byte salt -> base64, embedded in the local-dev key_reference.
_SALT_B64 = base64.b64encode(b"0123456789abcdef0123456789abcdef").decode("ascii")
KEY_REFERENCE = f"local-dev://{KEY_ID}#{_SALT_B64}"

HISTORY_IDS = {
    PATIENT_CONSENTED: [
        "aaaaaaaa-0000-4000-8000-00000000c001",
        "aaaaaaaa-0000-4000-8000-00000000c002",
    ],
    PATIENT_NO_CONSENT: [
        "aaaaaaaa-0000-4000-8000-00000000d001",
    ],
}


def _connect() -> psycopg.Connection:
    return psycopg.connect(get_settings().database_url, row_factory=dict_row, autocommit=True)


def _seed() -> None:
    with _connect() as conn, conn.cursor() as cur:
        # Tenant + user.
        cur.execute(
            """
            INSERT INTO platform.tenants
                (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (%s, 'AIPHITEST', 'AI PHI Test Clinic', 'AI PHI Test', 'clinic',
                    'ai-phi-test@docslot.test', '+919000000001', 'active')
            ON CONFLICT (tenant_id) DO UPDATE SET status = 'active', deleted_at = NULL
            """,
            (TENANT_ID,),
        )
        cur.execute(
            """
            INSERT INTO platform.users (user_id, email, full_name, password_hash)
            VALUES (%s, 'ai-phi-doctor@docslot.test', 'AI PHI Test Doctor', '!seed-no-login')
            ON CONFLICT (user_id) DO NOTHING
            """,
            (USER_ID,),
        )
        # Assign the seeded 'doctor' role (holds docslot.medical_history.read) to the
        # user IN this tenant — a TENANT role assignment is required for the
        # tenant-scoped perm to resolve (resolve_user_permissions keying).
        cur.execute(
            """
            INSERT INTO platform.user_tenant_roles (user_id, role_id, tenant_id)
            SELECT %s, r.role_id, %s FROM platform.roles r
            WHERE r.role_key = 'doctor'
              AND NOT EXISTS (
                  SELECT 1 FROM platform.user_tenant_roles utr
                  WHERE utr.user_id = %s AND utr.role_id = r.role_id AND utr.tenant_id = %s
              )
            """,
            (USER_ID, TENANT_ID, USER_ID, TENANT_ID),
        )
        # Active local-dev medical_history key for the tenant (simulates the
        # .NET/KMS having provisioned it; the AI service never provisions).
        cur.execute(
            """
            INSERT INTO platform.encryption_keys
                (key_id, tenant_id, data_class, key_reference, kms_provider, key_algorithm, key_version, status, activated_at, created_at)
            VALUES (%s, %s, 'medical_history', %s, 'local_dev', 'AES_256_GCM', 1, 'active', NOW(), NOW())
            ON CONFLICT (tenant_id, data_class, key_version) DO NOTHING
            """,
            (KEY_ID, TENANT_ID, KEY_REFERENCE),
        )
        # Patients: one consented, one not. docslot.patients is cross-tenant.
        for pid, phone, consented in (
            (PATIENT_CONSENTED, "+919000000100", True),
            (PATIENT_NO_CONSENT, "+919000000200", False),
        ):
            cur.execute(
                """
                INSERT INTO docslot.patients (patient_id, phone_number, full_name, consent_given_at, is_active)
                VALUES (%s, %s, %s, %s, true)
                ON CONFLICT (patient_id) DO UPDATE
                    SET consent_given_at = EXCLUDED.consent_given_at, is_active = true
                """,
                (pid, phone, f"Patient {phone}",
                 datetime.now(timezone.utc) if consented else None),
            )
            cur.execute(
                """
                INSERT INTO docslot.patient_tenant_links (patient_id, tenant_id)
                VALUES (%s, %s) ON CONFLICT DO NOTHING
                """,
                (pid, TENANT_ID),
            )
        # Plaintext medical history (the realistic dev seed: history is written
        # plaintext here; the slice proves ai.embeddings is encrypted at rest).
        histories = {
            HISTORY_IDS[PATIENT_CONSENTED][0]: (
                PATIENT_CONSENTED, "allergy", "Penicillin allergy",
                "Anaphylaxis to amoxicillin in 2021; carries an epi-pen.", "severe", "T78.2",
            ),
            HISTORY_IDS[PATIENT_CONSENTED][1]: (
                PATIENT_CONSENTED, "chronic_condition", "Essential hypertension",
                "Diagnosed 2022; on amlodipine 5mg once daily.", "moderate", "I10",
            ),
            HISTORY_IDS[PATIENT_NO_CONSENT][0]: (
                PATIENT_NO_CONSENT, "chronic_condition", "Type 2 diabetes",
                "HbA1c 8.1; metformin 500mg BD.", "moderate", "E11",
            ),
        }
        for hid, (pid, rtype, title, desc, sev, icd) in histories.items():
            cur.execute(
                """
                INSERT INTO docslot.patient_medical_history
                    (history_id, patient_id, tenant_id, record_type, title, description,
                     severity, icd10_code, is_active, is_critical)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, true, false)
                ON CONFLICT (history_id) DO NOTHING
                """,
                (hid, pid, TENANT_ID, rtype, title, desc, sev, icd),
            )


def _clean_phi_leaf_rows() -> None:
    """Delete per-test PHI leaf rows for the test tenant (keeps tenant/user/key)."""
    with _connect() as conn, conn.cursor() as cur:
        cur.execute("DELETE FROM platform.break_glass_grants WHERE tenant_id = %s", (TENANT_ID,))
        cur.execute("DELETE FROM platform.purpose_of_use_log WHERE tenant_id = %s", (TENANT_ID,))
        cur.execute("DELETE FROM platform.key_usage_log WHERE tenant_id = %s", (TENANT_ID,))
        cur.execute("DELETE FROM ai.embeddings WHERE tenant_id = %s", (TENANT_ID,))
        cur.execute("DELETE FROM ai.ai_document_extractions WHERE tenant_id = %s", (TENANT_ID,))
        cur.execute("DELETE FROM ai.ai_knowledge_bases WHERE tenant_id = %s", (TENANT_ID,))
        # Tenant-specific model configs (the platform-wide default, tenant_id NULL, stays).
        cur.execute("DELETE FROM ai.ai_model_configs WHERE tenant_id = %s", (TENANT_ID,))


@pytest.fixture(scope="session", autouse=True)
def _seeded() -> Iterator[None]:
    _seed()
    yield
    _clean_phi_leaf_rows()


@pytest.fixture(autouse=True)
def _clean() -> Iterator[None]:
    _clean_phi_leaf_rows()
    model_config.reset_cache()  # the model-config cache is process-global; isolate per test
    yield


@pytest.fixture
def db() -> Iterator[psycopg.Connection]:
    conn = _connect()
    try:
        yield conn
    finally:
        conn.close()


@pytest.fixture
def client() -> Iterator[TestClient]:
    with TestClient(app) as c:
        yield c


def make_token(user_id: str = USER_ID, tenant_id: str = TENANT_ID) -> str:
    s = get_settings()
    key = base64.b64decode(s.jwt_signing_key_b64)
    now = int(time.time())
    return jwt.encode(
        {
            "sub": user_id,
            "tenant_id": tenant_id,
            "iss": s.jwt_issuer,
            "aud": s.jwt_audience,
            "iat": now,
            "exp": now + 3600,
        },
        key,
        algorithm=s.jwt_algorithm,
    )


@pytest.fixture
def auth() -> dict[str, str]:
    return {
        "Authorization": f"Bearer {make_token()}",
        "X-Purpose-Of-Use": "treatment",
    }
