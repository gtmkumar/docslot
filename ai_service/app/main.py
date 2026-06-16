"""FastAPI application wiring for the DocSlot AI sibling service."""
from __future__ import annotations

import logging

from fastapi import FastAPI

from .config import get_settings
from .routers import extractions, health, predictions, rag, triage

logging.basicConfig(level=logging.INFO)

settings = get_settings()

app = FastAPI(
    title="DocSlot AI Service",
    version="1.0.0",
    description=(
        "AI sibling to the DocSlot .NET transactional service. Shares the "
        "PostgreSQL system of record; provides no-show prediction and "
        "RAG over patient medical history."
    ),
)

# /health (no auth) at root.
app.include_router(health.router)
# Authenticated AI endpoints under /ai/v1.
app.include_router(predictions.router, prefix=settings.api_prefix)
app.include_router(rag.router, prefix=settings.api_prefix)
app.include_router(extractions.router, prefix=settings.api_prefix)
app.include_router(triage.router, prefix=settings.api_prefix)
