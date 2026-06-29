"""PHI-at-rest field encryption for the AI service.

Byte-for-byte interoperable with the .NET ``FieldEncryptionService`` +
``LocalEnvelopeKeyManagementService`` over the SAME ``platform.encryption_keys``
rows, so AI-written ciphertext and .NET-written ciphertext share one KMS key and
are both rendered unrecoverable by the same DPDP cryptographic-erasure (key
destruction). The AI service NEVER provisions keys — provisioning stays a
.NET/KMS responsibility; if no active key exists this module FAILS CLOSED (it
raises rather than storing plaintext).

Envelope format (identical to the .NET ``Envelope`` record serialized with
``JsonSerializerDefaults.Web``):

    payload = base64( utf8( {"keyId", "wrappedKey", "nonce", "ciphertext", "tag"} ) )

* per-record data key (32 random bytes), AES-256-GCM over the plaintext ->
  ``ciphertext`` + 16-byte ``tag``, random 12-byte ``nonce``.
* the data key is wrapped under a KEK = PBKDF2-SHA256(passphrase, salt, 100_000,
  32) where the salt is embedded in the key's ``key_reference``
  (``local-dev://{key_id}#{base64(salt)}``); ``wrappedKey`` lays out as
  ``nonce(12) || tag(16) || ciphertext`` — exactly the .NET ``Concat`` order.

Every encrypt/decrypt is logged to ``platform.key_usage_log`` for forensics,
within the caller's transaction (pass the active ``psycopg`` connection).
"""
from __future__ import annotations

import base64
import hashlib
import json
import secrets
from dataclasses import dataclass
from typing import Any, Callable

import psycopg
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

from .config import get_settings

# The data_class shared with the source PHI (patient_medical_history.*). The AI
# columns are registered under it in platform.encrypted_fields_registry.
DATA_CLASS = "medical_history"

_PBKDF2_ITERATIONS = 100_000
_KEK_LEN = 32
_DATA_KEY_LEN = 32
_NONCE_LEN = 12
_TAG_LEN = 16


class EncryptionError(RuntimeError):
    """Base for encryption failures. Fail-closed: never silently store plaintext."""


class EncryptionKeyUnavailable(EncryptionError):
    """No active KMS key for the (tenant, data_class) — refuse to write PHI."""


class KeyDestroyed(EncryptionError):
    """The wrapping key was cryptographically destroyed (DPDP §12 erasure)."""


@dataclass(frozen=True)
class EncryptionContext:
    """Forensic context written to platform.key_usage_log per crypto op."""

    user_id: str | None
    tenant_id: str | None
    resource_type: str | None  # 'medical_history' (RAG) | 'lab_report' (OCR)
    resource_id: str | None  # patient_id
    ip_address: str | None = None


@dataclass(frozen=True)
class _KeyRef:
    key_id: str
    key_reference: str
    kms_provider: str
    is_destroyed: bool


def _passphrase() -> bytes:
    return get_settings().encryption_passphrase.encode("utf-8")


def _derive_kek(key: _KeyRef) -> bytes:
    """Derive the 256-bit KEK from the master passphrase + the key's salt.

    Mirrors LocalEnvelopeKeyManagementService.DeriveKek: the salt is the base64
    segment after ``#`` in key_reference; a destroyed key's reference ends in
    ``#DESTROYED`` (the salt was scrubbed) so the KEK can never be re-derived.
    """
    if key.kms_provider != "local_dev":
        raise EncryptionError(
            f"AI service supports only the 'local_dev' KMS provider; key "
            f"{key.key_id} uses {key.kms_provider!r} (provision via .NET/KMS)."
        )
    ref = key.key_reference
    hash_index = ref.find("#")
    salt_part = ref[hash_index + 1 :] if hash_index >= 0 else key.key_id
    if salt_part == "DESTROYED":
        raise KeyDestroyed(f"Key {key.key_id} destroyed; KEK cannot be derived.")
    try:
        salt = base64.b64decode(salt_part, validate=True)
    except ValueError:
        # Mirror .NET SafeFromBase64: malformed salt -> sha256(key_id) fallback.
        salt = hashlib.sha256(key.key_id.encode("utf-8")).digest()
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=_KEK_LEN,
        salt=salt,
        iterations=_PBKDF2_ITERATIONS,
    )
    return kdf.derive(_passphrase())


def _wrap_data_key(kek: bytes, data_key: bytes) -> bytes:
    """AES-GCM wrap of the data key under the KEK. Layout: nonce||tag||ciphertext."""
    nonce = secrets.token_bytes(_NONCE_LEN)
    out = AESGCM(kek).encrypt(nonce, data_key, None)  # cryptography appends tag
    ciphertext, tag = out[:-_TAG_LEN], out[-_TAG_LEN:]
    return nonce + tag + ciphertext


def _unwrap_data_key(kek: bytes, wrapped: bytes) -> bytes:
    nonce = wrapped[:_NONCE_LEN]
    tag = wrapped[_NONCE_LEN : _NONCE_LEN + _TAG_LEN]
    ciphertext = wrapped[_NONCE_LEN + _TAG_LEN :]
    return AESGCM(kek).decrypt(nonce, ciphertext + tag, None)


def _encrypt_to_envelope(plain: bytes, key: _KeyRef) -> str:
    """Envelope-encrypt raw bytes under ``key``; return the base64 payload string."""
    kek = _derive_kek(key)
    data_key = secrets.token_bytes(_DATA_KEY_LEN)
    wrapped = _wrap_data_key(kek, data_key)
    nonce = secrets.token_bytes(_NONCE_LEN)
    out = AESGCM(data_key).encrypt(nonce, plain, None)
    ciphertext, tag = out[:-_TAG_LEN], out[-_TAG_LEN:]
    envelope = {
        "keyId": str(key.key_id),
        "wrappedKey": base64.b64encode(wrapped).decode("ascii"),
        "nonce": base64.b64encode(nonce).decode("ascii"),
        "ciphertext": base64.b64encode(ciphertext).decode("ascii"),
        "tag": base64.b64encode(tag).decode("ascii"),
    }
    body = json.dumps(envelope, separators=(",", ":")).encode("utf-8")
    return base64.b64encode(body).decode("ascii")


def _decrypt_from_envelope(payload: str, resolve: Callable[[str], _KeyRef | None]) -> bytes:
    """Decrypt a base64 envelope payload. Fails closed if the key was destroyed."""
    try:
        env: dict[str, Any] = json.loads(base64.b64decode(payload).decode("utf-8"))
        key_id = str(env["keyId"])
        wrapped = base64.b64decode(env["wrappedKey"])
        nonce = base64.b64decode(env["nonce"])
        ciphertext = base64.b64decode(env["ciphertext"])
        tag = base64.b64decode(env["tag"])
    except Exception as exc:  # noqa: BLE001 — any malformed envelope is fatal
        raise EncryptionError("Malformed encryption envelope.") from exc

    key = resolve(key_id)
    if key is None:
        raise EncryptionError(f"Encryption key {key_id} not found.")
    if key.is_destroyed:
        raise KeyDestroyed(
            f"Key {key_id} cryptographically destroyed; data is unrecoverable."
        )
    kek = _derive_kek(key)
    data_key = _unwrap_data_key(kek, wrapped)
    return AESGCM(data_key).decrypt(nonce, ciphertext + tag, None)


class FieldEncryptor:
    """Encrypt/decrypt PHI inside the caller's DB transaction.

    Resolve the active key + write the forensic key_usage_log row on the SAME
    ``conn`` as the PHI write, so a rolled-back PHI insert also rolls back its
    usage log. The connection's row factory must be ``dict_row`` (the service
    default; see ai_service/app/db.py).
    """

    def __init__(self, conn: psycopg.Connection, ctx: EncryptionContext) -> None:
        self._conn = conn
        self._ctx = ctx

    # -- public API -------------------------------------------------------
    def encrypt_text(self, plaintext: str) -> tuple[str, str]:
        """Encrypt UTF-8 text. Returns (envelope_payload, key_id). Fail-closed."""
        key = self._active_key()
        payload = _encrypt_to_envelope(plaintext.encode("utf-8"), key)
        self._log_usage(key.key_id, "encrypt")
        return payload, key.key_id

    def encrypt_blob(self, raw: bytes) -> tuple[str, str]:
        """Encrypt raw bytes (e.g. a float32 vector). Returns (payload, key_id)."""
        key = self._active_key()
        payload = _encrypt_to_envelope(raw, key)
        self._log_usage(key.key_id, "encrypt")
        return payload, key.key_id

    def decrypt_text(self, payload: str) -> str:
        plain = _decrypt_from_envelope(payload, self._key_by_id)
        self._log_usage_for_payload(payload, "decrypt")
        return plain.decode("utf-8")

    def decrypt_blob(self, payload: str) -> bytes:
        plain = _decrypt_from_envelope(payload, self._key_by_id)
        self._log_usage_for_payload(payload, "decrypt")
        return plain

    # -- key resolution ---------------------------------------------------
    def _active_key(self) -> _KeyRef:
        with self._conn.cursor() as cur:
            cur.execute(
                """
                SELECT key_id, key_reference, kms_provider
                FROM platform.encryption_keys
                WHERE data_class = %(dc)s AND status = 'active'
                  AND (tenant_id = %(t)s OR (%(t)s IS NULL AND tenant_id IS NULL))
                ORDER BY key_version DESC
                LIMIT 1
                """,
                {"dc": DATA_CLASS, "t": self._ctx.tenant_id},
            )
            row = cur.fetchone()
        if row is None:
            raise EncryptionKeyUnavailable(
                f"No active '{DATA_CLASS}' encryption key for tenant "
                f"{self._ctx.tenant_id}; refusing to write PHI (provision via .NET/KMS)."
            )
        return _KeyRef(str(row["key_id"]), row["key_reference"], row["kms_provider"], False)

    def _key_by_id(self, key_id: str) -> _KeyRef | None:
        with self._conn.cursor() as cur:
            cur.execute(
                """
                SELECT key_id, key_reference, kms_provider,
                       (status = 'destroyed' OR destroyed_at IS NOT NULL) AS is_destroyed
                FROM platform.encryption_keys
                WHERE key_id = %(kid)s
                """,
                {"kid": key_id},
            )
            row = cur.fetchone()
        if row is None:
            return None
        return _KeyRef(
            str(row["key_id"]), row["key_reference"], row["kms_provider"], bool(row["is_destroyed"])
        )

    # -- forensic logging -------------------------------------------------
    def _log_usage(self, key_id: str, operation: str) -> None:
        with self._conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.key_usage_log (
                    usage_id, key_id, operation, user_id, tenant_id,
                    resource_type, resource_id, ip_address, success, error_message, occurred_at
                ) VALUES (
                    gen_random_uuid(), %(kid)s, %(op)s, %(uid)s, %(tid)s,
                    %(rt)s, %(rid)s, CAST(%(ip)s AS inet), true, NULL, NOW()
                )
                """,
                {
                    "kid": key_id,
                    "op": operation,
                    "uid": self._ctx.user_id,
                    "tid": self._ctx.tenant_id,
                    "rt": self._ctx.resource_type,
                    "rid": self._ctx.resource_id,
                    "ip": self._ctx.ip_address,
                },
            )

    def _log_usage_for_payload(self, payload: str, operation: str) -> None:
        """Log a decrypt against the key recorded in the envelope (best-effort id)."""
        try:
            env = json.loads(base64.b64decode(payload).decode("utf-8"))
            key_id = str(env["keyId"])
        except Exception:  # noqa: BLE001 — already decrypted; logging must not raise
            return
        self._log_usage(key_id, operation)
