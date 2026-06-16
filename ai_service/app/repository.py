"""Data access for the no-show workflow.

EVERY query is tenant-scoped: the owner connection bypasses RLS, so the
`tenant_id = %(tenant_id)s` filter is the ONLY isolation guard. Do not remove it.
"""
from __future__ import annotations

import json
import logging
from datetime import date

from .db import get_connection
from .features import BookingFeatureInput

logger = logging.getLogger("ai_service.repository")

# Statuses that count as positive/negative labels for training.
_NO_SHOW = ("no_show",)
_KEPT = ("completed", "confirmed")


def _to_feature_input(row: dict) -> BookingFeatureInput:
    return BookingFeatureInput(
        booking_id=str(row["booking_id"]),
        booked_via=row["booked_via"],
        slot_date=row["slot_date"],
        start_time=row["start_time"],
        age=row["age"],
        booked_at=row["booked_at"],
        has_notes=bool(row["notes"]),
        prior_total=int(row["prior_total"]),
        prior_no_shows=int(row["prior_no_shows"]),
    )


# Shared SELECT that joins slot + patient and computes the patient's prior history
# (strictly before this booking's booked_at, same tenant). Always tenant-scoped.
_BASE_SELECT = """
SELECT
    b.booking_id,
    b.tenant_id,
    b.patient_id,
    b.status,
    b.booked_via,
    b.booked_at,
    b.notes,
    ts.slot_date,
    ts.start_time,
    COALESCE(p.age, b.patient_age_at_booking) AS age,
    (
        SELECT count(*) FROM docslot.bookings pb
        WHERE pb.tenant_id = b.tenant_id
          AND pb.patient_id = b.patient_id
          AND pb.booked_at < b.booked_at
    ) AS prior_total,
    (
        SELECT count(*) FROM docslot.bookings pb
        WHERE pb.tenant_id = b.tenant_id
          AND pb.patient_id = b.patient_id
          AND pb.booked_at < b.booked_at
          AND pb.status = 'no_show'
    ) AS prior_no_shows
FROM docslot.bookings b
JOIN docslot.time_slots ts ON ts.slot_id = b.slot_id
LEFT JOIN docslot.patients p ON p.patient_id = b.patient_id
"""


def fetch_booking(tenant_id: str, booking_id: str) -> BookingFeatureInput | None:
    """Tenant-scoped single-booking lookup. None if not found in this tenant."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            _BASE_SELECT + " WHERE b.tenant_id = %(tenant_id)s AND b.booking_id = %(booking_id)s",
            {"tenant_id": tenant_id, "booking_id": booking_id},
        )
        row = cur.fetchone()
    return _to_feature_input(row) if row else None


def fetch_today_bookings(tenant_id: str, on: date) -> list[BookingFeatureInput]:
    """Tenant-scoped bookings for `on` with status in (pending, confirmed)."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            _BASE_SELECT
            + """ WHERE b.tenant_id = %(tenant_id)s
                    AND ts.slot_date = %(on)s
                    AND b.status IN ('pending', 'confirmed')
                  ORDER BY ts.start_time""",
            {"tenant_id": tenant_id, "on": on},
        )
        rows = cur.fetchall()
    return [_to_feature_input(r) for r in rows]


def fetch_training_rows(tenant_id: str) -> list[BookingFeatureInput]:
    """Tenant-scoped historical labeled bookings (no_show / completed / confirmed)."""
    statuses = list(_NO_SHOW + _KEPT)
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            _BASE_SELECT
            + " WHERE b.tenant_id = %(tenant_id)s AND b.status = ANY(%(statuses)s)",
            {"tenant_id": tenant_id, "statuses": statuses},
        )
        rows = cur.fetchall()
    return [_to_feature_input(r) for r in rows]


def label_of(status: str) -> int | None:
    if status in _NO_SHOW:
        return 1
    if status in _KEPT:
        return 0
    return None


def insert_prediction(
    *,
    tenant_id: str,
    model_name: str,
    model_version: str,
    booking_id: str,
    predicted_value: float,
    features_used: dict,
    confidence_interval: dict | None,
    valid_until,
) -> str:
    """Insert one ai.ai_predictions row (tenant-scoped). Returns prediction_id."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO ai.ai_predictions (
                tenant_id, model_name, model_version, prediction_type,
                related_resource_type, related_resource_id, predicted_value,
                confidence_interval, features_used, valid_until
            ) VALUES (
                %(tenant_id)s, %(model_name)s, %(model_version)s, 'no_show_probability',
                'booking', %(booking_id)s, %(predicted_value)s,
                %(confidence_interval)s, %(features_used)s, %(valid_until)s
            )
            RETURNING prediction_id
            """,
            {
                "tenant_id": tenant_id,
                "model_name": model_name,
                "model_version": model_version,
                "booking_id": booking_id,
                "predicted_value": predicted_value,
                "confidence_interval": json.dumps(confidence_interval)
                if confidence_interval is not None
                else None,
                "features_used": json.dumps(features_used),
                "valid_until": valid_until,
            },
        )
        row = cur.fetchone()
    return str(row["prediction_id"])


def write_audit_best_effort(
    *,
    tenant_id: str,
    user_id: str,
    booking_id: str,
    action: str,
    purpose: str,
) -> None:
    """Best-effort audit write. The audit_log is hash-chained via trigger; any
    failure is logged and swallowed so it never blocks a prediction."""
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.audit_log (
                    user_id, tenant_id, action, resource_type, resource_id,
                    purpose, legal_basis, success
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, %(action)s, 'booking', %(resource_id)s,
                    %(purpose)s, 'legitimate_interest', true
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "action": action,
                    "resource_id": booking_id,
                    "purpose": purpose,
                },
            )
    except Exception as exc:  # noqa: BLE001 — intentional best-effort
        logger.warning("Audit write failed (continuing): %s", exc)
