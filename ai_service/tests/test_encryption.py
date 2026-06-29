"""PHI-at-rest encryption: round-trip, envelope shape, fail-closed, key destruction."""
from __future__ import annotations

import base64
import json
import uuid

import numpy as np
import psycopg
import pytest

from app import ocr_repository
from app.encryption import (
    EncryptionContext,
    EncryptionKeyUnavailable,
    FieldEncryptor,
    KeyDestroyed,
)
from conftest import KEY_ID, PATIENT_CONSENTED, TENANT_ID, _connect


def _ctx(tenant_id: str = TENANT_ID) -> EncryptionContext:
    return EncryptionContext(
        user_id=None, tenant_id=tenant_id, resource_type="medical_history", resource_id=PATIENT_CONSENTED
    )


def test_text_round_trip_and_envelope_shape(db: psycopg.Connection) -> None:
    enc = FieldEncryptor(db, _ctx())
    plaintext = "Penicillin allergy — anaphylaxis to amoxicillin (2021). [PHI]"
    payload, key_id = enc.encrypt_text(plaintext)

    # Ciphertext at rest is NOT the plaintext and does not contain it.
    assert payload != plaintext
    assert "Penicillin" not in payload
    assert key_id == KEY_ID

    # Envelope is base64( utf8( {keyId, wrappedKey, nonce, ciphertext, tag} ) ),
    # camelCase keys — byte-for-byte the .NET FieldEncryptionService format.
    env = json.loads(base64.b64decode(payload).decode("utf-8"))
    assert set(env) == {"keyId", "wrappedKey", "nonce", "ciphertext", "tag"}
    assert env["keyId"] == KEY_ID
    # wrappedKey = nonce(12) || tag(16) || wrapped-data-key(32) = 60 bytes.
    assert len(base64.b64decode(env["wrappedKey"])) == 60

    assert enc.decrypt_text(payload) == plaintext


def test_vector_blob_round_trip(db: psycopg.Connection) -> None:
    enc = FieldEncryptor(db, _ctx())
    vec = np.asarray([0.11, -0.22, 0.33, 0.44, 0.55], dtype="<f4")
    payload, _ = enc.encrypt_blob(vec.tobytes())
    # The raw float bytes are not stored in the clear.
    assert vec.tobytes() not in base64.b64decode(payload)
    recovered = np.frombuffer(enc.decrypt_blob(payload), dtype="<f4")
    assert np.allclose(recovered, vec)


def test_encrypt_logs_key_usage(db: psycopg.Connection) -> None:
    enc = FieldEncryptor(db, _ctx())
    enc.encrypt_text("forensic check")
    with db.cursor() as cur:
        cur.execute(
            "SELECT count(*) AS n FROM platform.key_usage_log WHERE key_id = %s AND tenant_id = %s AND operation = 'encrypt'",
            (KEY_ID, TENANT_ID),
        )
        assert cur.fetchone()["n"] >= 1


def test_fail_closed_when_no_active_key(db: psycopg.Connection) -> None:
    # A tenant with no provisioned key must refuse to write PHI (never plaintext).
    enc = FieldEncryptor(db, _ctx(tenant_id=str(uuid.uuid4())))
    with pytest.raises(EncryptionKeyUnavailable):
        enc.encrypt_text("must not be stored")


def test_destroyed_key_fails_closed_on_decrypt() -> None:
    """Cryptographic erasure (DPDP §12): a destroyed key makes data unrecoverable."""
    tt = str(uuid.uuid4())
    kk = str(uuid.uuid4())
    salt = base64.b64encode(b"destroy-test-salt-0123456789abcd").decode("ascii")
    conn = _connect()
    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.tenants
                    (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone)
                VALUES (%s, %s, 'Destroy Test', 'Destroy', 'clinic', %s, '+919000009999')
                """,
                (tt, f"DESTROY-{kk[:8]}", f"destroy-{kk[:8]}@docslot.test"),
            )
            cur.execute(
                """
                INSERT INTO platform.encryption_keys
                    (key_id, tenant_id, data_class, key_reference, kms_provider, key_algorithm, key_version, status, activated_at, created_at)
                VALUES (%s, %s, 'medical_history', %s, 'local_dev', 'AES_256_GCM', 1, 'active', NOW(), NOW())
                """,
                (kk, tt, f"local-dev://{kk}#{salt}"),
            )

        ctx = EncryptionContext(user_id=None, tenant_id=tt, resource_type="medical_history", resource_id=None)
        payload, _ = FieldEncryptor(conn, ctx).encrypt_text("top secret diagnosis")

        # Destroy the key: scrub the wrapping salt so the KEK can never be re-derived.
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE platform.encryption_keys
                SET status = 'destroyed', destroyed_at = NOW(),
                    key_reference = split_part(key_reference, '#', 1) || '#DESTROYED'
                WHERE key_id = %s
                """,
                (kk,),
            )

        with pytest.raises(KeyDestroyed):
            FieldEncryptor(conn, ctx).decrypt_text(payload)
    finally:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM platform.key_usage_log WHERE key_id = %s", (kk,))
            cur.execute("DELETE FROM platform.encryption_keys WHERE key_id = %s", (kk,))
            cur.execute("DELETE FROM platform.tenants WHERE tenant_id = %s", (tt,))
        conn.close()


def test_ocr_raw_text_encrypted_at_rest(db: psycopg.Connection) -> None:
    """ai.ai_document_extractions.raw_ocr_text is stored encrypted + decryptable."""
    raw = "CBC PANEL\nHemoglobin 9.1 g/dL LOW\nWBC 12.3 HIGH\n[PHI lab values]"
    extraction_id = ocr_repository.insert_extraction(
        tenant_id=TENANT_ID,
        source_url="/tmp/labreport.png",
        source_mime_type="image/png",
        source_size_bytes=1234,
        related_patient_id=PATIENT_CONSENTED,
        related_booking_id=None,
        ocr_engine="tesseract-5.5.2",
        raw_ocr_text=raw,
        extraction_model="docslot-labparser-v1",
        extracted_data={"abnormalCount": 2},
        overall_confidence=0.91,
        requires_human_review=True,
        status="extracted",
        user_id=None,
    )
    with db.cursor() as cur:
        cur.execute(
            "SELECT raw_ocr_text, encryption_key_id FROM ai.ai_document_extractions WHERE extraction_id = %s",
            (extraction_id,),
        )
        row = cur.fetchone()
    # Stored value is the encrypted envelope, not the raw lab text.
    assert "Hemoglobin" not in row["raw_ocr_text"]
    assert str(row["encryption_key_id"]) == KEY_ID
    env = json.loads(base64.b64decode(row["raw_ocr_text"]).decode("utf-8"))
    assert env["keyId"] == KEY_ID
    # And it round-trips through the decrypting read path.
    assert ocr_repository.get_extraction_raw_text(extraction_id, TENANT_ID) == raw
