"""Application configuration via pydantic-settings.

All values are overridable via environment variables (or a local .env file).
The dev defaults match the verified integration facts for the DocSlot platform.
"""
from __future__ import annotations

from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="AI_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # --- Service ---
    service_name: str = "docslot-ai-service"
    api_prefix: str = "/ai/v1"

    # --- Database ---
    # Owner connection (dev): local trust, no password. Bypasses RLS, so tenant
    # isolation is enforced in application code (every query filters tenant_id).
    # Production would use a dedicated least-privilege `docslot_ai` role with RLS
    # + `SET LOCAL app.tenant_id`. See README.
    database_url: str = "postgresql://gtmkumar@localhost:5432/docslot_platform"

    # --- JWT ---
    # DocSlot issues HS256 tokens. The HMAC key is the base64-DECODED bytes of the
    # signing key (the .NET side does Convert.FromBase64String(SigningKey)).
    jwt_signing_key_b64: str = (
        "ZGV2LW9ubHktc2lnbmluZy1rZXktcmVwbGFjZS1pbi1wcm9kdWN0aW9uLTI1Ni1iaXQh"
    )
    jwt_issuer: str = "docslot-platform"
    jwt_audience: str = "docslot-clients"
    jwt_algorithm: str = "HS256"

    # --- OCR (lab-report extraction) ---
    # pytesseract shells out to the tesseract binary; set the absolute path so it
    # works even when /opt/homebrew/bin is not on PATH.
    tesseract_cmd: str = "/opt/homebrew/bin/tesseract"
    ocr_engine: str = "tesseract-5.5.2"
    labparser_model: str = "docslot-labparser-v1"
    # Below this OCR confidence the extraction is flagged for human review.
    ocr_review_confidence_threshold: float = 0.85
    # Directory where generated sample documents are written.
    samples_dir: str = "samples"

    # --- ML ---
    # Minimum labeled samples required before training a real classifier;
    # below this (or single-class) we fall back to a transparent heuristic.
    min_training_samples: int = 20
    model_name: str = "docslot-noshow"

    # --- RAG / LLM (optional synthesis) ---
    # If an API key is set, /ai/v1/rag/ask MAY synthesize the answer with an LLM
    # (httpx call). Unset (the DEFAULT) -> fully-offline extractive answers.
    # Read from AI_LLM_API_KEY or AI_OPENAI_API_KEY.
    llm_api_key: str | None = None
    openai_api_key: str | None = None
    llm_base_url: str = "https://api.anthropic.com"
    llm_model: str = "claude-sonnet-4-5"


@lru_cache
def get_settings() -> Settings:
    return Settings()
