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
    # 'user' | 'client' | 'service'. A 'service' token is a non-human, short-TTL, .NET-minted
    # service identity (e.g. the no-show backfill worker). It is DENIED BY DEFAULT at the auth
    # layer (get_principal): only the explicit non-PHI no-show allow-list
    # (get_principal_allow_service) admits it, so a service identity can never reach a PHI/ops
    # endpoint. enforce_phi_gate ALSO refuses it as defense-in-depth.
    token_use: str = "user"

    @property
    def is_service(self) -> bool:
        return self.token_use == "service"


def _signing_key(settings: Settings) -> bytes:
    return base64.b64decode(settings.jwt_signing_key_b64)


def _principal_from_credentials(
    credentials: HTTPAuthorizationCredentials | None,
    settings: Settings,
) -> Principal:
    """Decode + validate the bearer JWT into a Principal (raises 401/403).

    Does NOT apply the service-token wall — the caller picks the policy: get_principal
    (default-deny a service identity) or get_principal_allow_service (the no-show allow-list).

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
        token_use=str(claims.get("token_use") or "user"),
    )


# A non-human service identity may never reach a PHI/ops endpoint. The wall lives HERE (the single
# auth chokepoint), not scattered per-router, so it is FAIL-CLOSED: every authenticated endpoint —
# including any added later — denies a service token by default; only the explicit non-PHI no-show
# allow-list (get_principal_allow_service) admits one.
_SERVICE_TOKEN_DENIED_DETAIL = "A service identity may not access this endpoint."


def get_principal(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
    settings: Settings = Depends(get_settings),
) -> Principal:
    """Default auth dependency. Returns a Principal or raises 401/403.

    FAIL-CLOSED PHI WALL: a token_use='service' identity (the .NET-minted no-show backfill
    worker) is REFUSED (403) here. Because every authenticated endpoint depends on this — and any
    endpoint added later does too — a service identity is denied PHI/ops by DEFAULT; only the
    explicit non-PHI no-show allow-list (get_principal_allow_service) admits it. This inverts the
    wall from per-endpoint opt-out (fragile — one missed router = a PHI leak) to global default-deny.

    - 401 if the Authorization header is missing/malformed or the token is invalid.
    - 403 if the token is valid but carries no tenant_id claim, OR is a service identity.
    """
    principal = _principal_from_credentials(credentials, settings)
    if principal.is_service:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail=_SERVICE_TOKEN_DENIED_DETAIL,
        )
    return principal


def get_principal_allow_service(
    credentials: HTTPAuthorizationCredentials | None = Depends(_bearer),
    settings: Settings = Depends(get_settings),
) -> Principal:
    """Auth dependency for the NON-PHI no-show SCORING allow-list ONLY. Admits user, client, AND
    service tokens: the backfill worker presents a service token; the on-demand path forwards a
    human caller's user token. MUST NOT guard any endpoint that can reach PHI — no-show scoring
    consumes only non-PHI booking features (lead time / slot hour / on-behalf). Pairs with the
    fail-closed default-deny in get_principal.
    """
    return _principal_from_credentials(credentials, settings)
