"""Token clock-skew leeway (issue #51).

The .NET API validates JWTs with ClockSkew = 30s. The AI service must honor the same
leeway, otherwise a forwarded user token the .NET API still accepts (within its skew)
is rejected here (401), making AI calls intermittently fail in the ~30s window after a
user's access token expires. We assert the boundary on the no-show endpoint (a valid
token → 404 for an unknown booking, proving auth passed; an over-expired token → 401).
"""
from __future__ import annotations

import base64
import time

import jwt
import psycopg
from fastapi.testclient import TestClient

from app.config import get_settings
from conftest import TENANT_ID, USER_ID

NO_SHOW = "/ai/v1/predictions/no-show"
UNKNOWN_BOOKING = "00000000-0000-0000-0000-000000000000"


def _token(exp_delta_seconds: int) -> str:
    """A user token whose exp is exp_delta_seconds from now (negative = already expired)."""
    s = get_settings()
    key = base64.b64decode(s.jwt_signing_key_b64)
    now = int(time.time())
    return jwt.encode(
        {
            "sub": USER_ID,
            "tenant_id": TENANT_ID,
            "iss": s.jwt_issuer,
            "aud": s.jwt_audience,
            "iat": now - 3600,
            "exp": now + exp_delta_seconds,
        },
        key,
        algorithm=s.jwt_algorithm,
    )


def _headers(tok: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {tok}"}


def test_valid_token_accepted(client: TestClient, db: psycopg.Connection) -> None:
    # Sanity: a normally-valid token authenticates → 404 (unknown booking), not 401.
    r = client.post(NO_SHOW, json={"bookingId": UNKNOWN_BOOKING}, headers=_headers(_token(3600)))
    assert r.status_code == 404, r.text


def test_token_expired_within_leeway_accepted(client: TestClient, db: psycopg.Connection) -> None:
    # Expired 10s ago — inside the 30s leeway (matches .NET ClockSkew) → still accepted.
    r = client.post(NO_SHOW, json={"bookingId": UNKNOWN_BOOKING}, headers=_headers(_token(-10)))
    assert r.status_code == 404, r.text


def test_token_expired_beyond_leeway_rejected(client: TestClient, db: psycopg.Connection) -> None:
    # Expired 60s ago — beyond the 30s leeway → 401 (matches .NET rejecting it too).
    r = client.post(NO_SHOW, json={"bookingId": UNKNOWN_BOOKING}, headers=_headers(_token(-60)))
    assert r.status_code == 401, r.text
