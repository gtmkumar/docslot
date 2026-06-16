"""JWT authentication — validates DocSlot-issued HS256 bearer tokens.

The HMAC key is the base64-DECODED bytes of the configured signing key, matching
the .NET side's `Convert.FromBase64String(SigningKey)`.
"""
from __future__ import annotations

import base64
from dataclasses import dataclass

import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from .config import Settings, get_settings

_bearer = HTTPBearer(auto_error=False)


@dataclass(frozen=True)
class Principal:
    """The authenticated caller, resolved from validated JWT claims."""

    user_id: str
    tenant_id: str
    broker_id: str | None = None


def _signing_key(settings: Settings) -> bytes:
    return base64.b64decode(settings.jwt_signing_key_b64)


def get_principal(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
    settings: Settings = Depends(get_settings),
) -> Principal:
    """FastAPI dependency. Returns a Principal or raises 401/403.

    - 401 if the Authorization header is missing/malformed or the token is invalid.
    - 403 if the token is valid but carries no tenant_id claim.
    """
    if credentials is None or not credentials.credentials:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Missing or malformed Authorization header.",
            headers={"WWW-Authenticate": "Bearer"},
        )

    try:
        claims = jwt.decode(
            credentials.credentials,
            _signing_key(settings),
            algorithms=[settings.jwt_algorithm],
            issuer=settings.jwt_issuer,
            audience=settings.jwt_audience,
            options={"require": ["exp", "sub"]},
        )
    except jwt.PyJWTError as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail=f"Invalid token: {exc}",
            headers={"WWW-Authenticate": "Bearer"},
        ) from exc

    user_id = claims.get("sub")
    tenant_id = claims.get("tenant_id")

    if not user_id:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Token missing subject (user) claim.",
            headers={"WWW-Authenticate": "Bearer"},
        )
    if not tenant_id:
        # Authenticated but no active tenant -> forbidden.
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Token has no active tenant_id claim.",
        )

    return Principal(
        user_id=str(user_id),
        tenant_id=str(tenant_id),
        broker_id=str(claims["broker_id"]) if claims.get("broker_id") else None,
    )
