"""Feature engineering for the no-show model.

All features are explainable: the dicts returned here are persisted to
ai.ai_predictions.features_used so the score is fully traceable.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime, time, timezone

# The booking channels we one-hot encode. 'unknown' catches anything else.
BOOKED_VIA_CHANNELS = ("whatsapp", "dashboard", "api", "walk_in", "phone_call")


@dataclass
class BookingFeatureInput:
    """Raw booking + patient + slot fields pulled from the DB."""

    booking_id: str
    booked_via: str
    slot_date: date
    start_time: time
    age: int | None
    booked_at: datetime | None
    has_notes: bool
    prior_total: int            # patient's prior bookings (this tenant)
    prior_no_shows: int         # of which were no-shows


def _slot_datetime(slot_date: date, start_time: time) -> datetime:
    return datetime.combine(slot_date, start_time)


def build_features(inp: BookingFeatureInput) -> dict:
    """Compute the explainable feature dict for one booking.

    Returns plain JSON-serializable values (used both for ML vectors and for
    features_used persistence).
    """
    slot_dt = _slot_datetime(inp.slot_date, inp.start_time)

    hour_of_day = inp.start_time.hour
    day_of_week = inp.slot_date.weekday()  # Mon=0 .. Sun=6

    # Lead time in hours (booked_at -> slot). Negative/None coerced to 0.
    lead_time_hours = 0.0
    if inp.booked_at is not None:
        booked = inp.booked_at
        slot_aware = slot_dt.replace(tzinfo=booked.tzinfo or timezone.utc)
        lead_time_hours = max(0.0, (slot_aware - booked).total_seconds() / 3600.0)

    age = inp.age if inp.age is not None else -1  # -1 sentinel = unknown

    prior_no_show_rate = (
        inp.prior_no_shows / inp.prior_total if inp.prior_total > 0 else 0.0
    )

    channel = inp.booked_via if inp.booked_via in BOOKED_VIA_CHANNELS else "unknown"

    feats = {
        "booked_via": channel,
        "hour_of_day": hour_of_day,
        "day_of_week": day_of_week,
        "age": age,
        "has_notes": bool(inp.has_notes),
        "lead_time_hours": round(lead_time_hours, 2),
        "prior_total_bookings": inp.prior_total,
        "prior_no_shows": inp.prior_no_shows,
        "prior_no_show_rate": round(prior_no_show_rate, 4),
        "is_early_morning": hour_of_day < 9,
        "is_new_patient": inp.prior_total == 0,
    }
    return feats


def vectorize(feats: dict) -> list[float]:
    """Turn an explainable feature dict into a numeric vector for sklearn.

    Order is stable and shared between training and inference.
    """
    one_hot = [1.0 if feats["booked_via"] == c else 0.0 for c in BOOKED_VIA_CHANNELS]
    return one_hot + [
        float(feats["hour_of_day"]),
        float(feats["day_of_week"]),
        float(feats["age"]),
        1.0 if feats["has_notes"] else 0.0,
        float(feats["lead_time_hours"]),
        float(feats["prior_no_show_rate"]),
        1.0 if feats["is_early_morning"] else 0.0,
        1.0 if feats["is_new_patient"] else 0.0,
    ]


FEATURE_NAMES: list[str] = [f"channel_{c}" for c in BOOKED_VIA_CHANNELS] + [
    "hour_of_day",
    "day_of_week",
    "age",
    "has_notes",
    "lead_time_hours",
    "prior_no_show_rate",
    "is_early_morning",
    "is_new_patient",
]
