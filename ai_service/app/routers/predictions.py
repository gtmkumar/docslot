"""No-show prediction endpoints. All require a valid DocSlot bearer JWT."""
from __future__ import annotations

import logging
import uuid
from datetime import datetime
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, status

from ..auth import Principal, get_principal
from ..config import get_settings
from ..features import BookingFeatureInput, build_features
from ..model import risk_band, score
from ..repository import (
    fetch_booking,
    fetch_today_bookings,
    insert_prediction,
    write_audit_best_effort,
)
from ..schemas import NoShowPrediction, NoShowRequest, NoShowTodayResponse

logger = logging.getLogger("ai_service.predictions")
router = APIRouter(prefix="/predictions", tags=["predictions"])

IST = ZoneInfo("Asia/Kolkata")


def _predict_and_persist(
    principal: Principal, booking: BookingFeatureInput
) -> NoShowPrediction:
    """Score one booking, persist the prediction + best-effort audit, return DTO."""
    settings = get_settings()
    feats = build_features(booking)
    result = score(principal.tenant_id, feats)

    # valid_until = the slot datetime (when the prediction stops being relevant).
    valid_until = datetime.combine(booking.slot_date, booking.start_time, tzinfo=IST)

    insert_prediction(
        tenant_id=principal.tenant_id,
        model_name=settings.model_name,
        model_version=result.model_version,
        booking_id=booking.booking_id,
        predicted_value=result.probability,
        features_used=feats,
        confidence_interval=result.confidence_interval,
        valid_until=valid_until,
    )

    write_audit_best_effort(
        tenant_id=principal.tenant_id,
        user_id=principal.user_id,
        booking_id=booking.booking_id,
        action="ai.predict.no_show",
        purpose="no_show_risk_scoring",
    )

    return NoShowPrediction(
        bookingId=booking.booking_id,
        noShowProbability=result.probability,
        riskBand=risk_band(result.probability),
        modelName=settings.model_name,
        modelVersion=result.model_version,
        featuresUsed=feats,
    )


@router.post("/no-show", response_model=NoShowPrediction)
def predict_no_show(
    body: NoShowRequest,
    principal: Principal = Depends(get_principal),
) -> NoShowPrediction:
    try:
        uuid.UUID(body.bookingId)
    except (ValueError, AttributeError, TypeError):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Booking not found in this tenant.",
        )
    booking = fetch_booking(principal.tenant_id, body.bookingId)
    if booking is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Booking not found in this tenant.",
        )
    return _predict_and_persist(principal, booking)


@router.get("/no-show/today", response_model=NoShowTodayResponse)
def predict_no_show_today(
    principal: Principal = Depends(get_principal),
) -> NoShowTodayResponse:
    today = datetime.now(IST).date()
    bookings = fetch_today_bookings(principal.tenant_id, today)
    predictions = [_predict_and_persist(principal, b) for b in bookings]
    return NoShowTodayResponse(
        date=today.isoformat(),
        count=len(predictions),
        predictions=predictions,
    )
