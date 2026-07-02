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
    # Clock-skew leeway (seconds) for token exp/nbf validation. MUST match the .NET
    # API's TokenValidationParameters.ClockSkew (30s) so the two services agree on a
    # token's validity window. Without it, a token the .NET API still accepts within
    # its skew is rejected here (401), making forwarded-JWT AI calls (triage/RAG/OCR)
    # intermittently fail in the ~30s window after a user's access token expires. (#51)
    jwt_leeway_seconds: int = 30

    # --- PHI-at-rest encryption ---
    # Master passphrase for envelope encryption of AI-derived PHI (RAG chunk text +
    # embedding vectors, raw OCR text). MUST equal the .NET Encryption:Passphrase so
    # the two services share one platform.encryption_keys key per tenant and DPDP key
    # destruction renders both services' ciphertext unrecoverable. The DB stores only
    # a per-key salt (key_reference), never this passphrase. Override in prod from a
    # secret manager (AI_ENCRYPTION_PASSPHRASE); the AI service NEVER provisions keys.
    encryption_passphrase: str = "dev-only-encryption-passphrase-replace-in-production"

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
    # DEV/DEMO ONLY: allow the lab-report endpoint to OCR a caller-supplied local
    # filesystem path (sourceUrl). Default OFF — a caller-controlled path is an
    # arbitrary-local-file read (not tenant-scoped) + a file existence/size oracle,
    # so production must leave this False (the .NET proxy never forwards sourceUrl;
    # it sends imageBase64). With this off, a request carrying sourceUrl is rejected
    # (400) and the path is never touched. Sample generation (no sourceUrl) is unaffected.
    allow_dev_source_paths: bool = False

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
