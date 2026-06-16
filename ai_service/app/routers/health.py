"""Health endpoint (no auth)."""
from __future__ import annotations

from fastapi import APIRouter

from ..config import get_settings
from ..db import check_db_connection
from ..schemas import HealthResponse

router = APIRouter()


@router.get("/health", response_model=HealthResponse, tags=["health"])
def health() -> HealthResponse:
    settings = get_settings()
    return HealthResponse(
        status="ok",
        service=settings.service_name,
        dbConnected=check_db_connection(),
    )
