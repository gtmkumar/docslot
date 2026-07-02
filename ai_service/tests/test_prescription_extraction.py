"""Prescription OCR extraction endpoint + pure parser unit tests.

Integration tests mirror the lab-report PHI gates (relatedPatientId required,
purpose header required, tenant link enforced, consent-or-break-glass) against the
live docslot_platform dev DB; the parser tests are pure (no DB, no OCR engine).
"""
from __future__ import annotations

import base64

import psycopg
from fastapi.testclient import TestClient

from app import prescription_parser, sample_docs
from conftest import (
    PATIENT_CONSENTED,
    PATIENT_NO_CONSENT,
    TENANT_ID,
    make_service_token,
    make_token,
)

EXTRACT = "/ai/v1/extractions/prescription"
UNLINKED_PATIENT = "aaaaaaaa-0000-4000-8000-0000000fffff"  # valid UUID, not tenant-linked


def _row(db: psycopg.Connection, patient_id: str) -> dict | None:
    with db.cursor() as cur:
        cur.execute(
            """
            SELECT source_type, requires_human_review, status, overall_confidence
            FROM ai.ai_document_extractions
            WHERE related_patient_id = %s AND tenant_id = %s
            ORDER BY created_at DESC LIMIT 1
            """,
            (patient_id, TENANT_ID),
        )
        return cur.fetchone()


# --- PHI gates --------------------------------------------------------------
def test_prescription_requires_patient_link(client: TestClient, auth: dict) -> None:
    # PHI must be patient-linked: no relatedPatientId -> 422 (never an orphan store).
    r = client.post(EXTRACT, json={}, headers=auth)
    assert r.status_code == 422, r.text


def test_prescription_missing_purpose_header_is_400(client: TestClient) -> None:
    # Same posture as the lab endpoint: the X-Purpose-Of-Use header is mandatory.
    headers = {"Authorization": f"Bearer {make_token()}"}
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_CONSENTED}, headers=headers)
    assert r.status_code == 400, r.text


def test_prescription_unknown_patient_is_404(client: TestClient, auth: dict) -> None:
    r = client.post(EXTRACT, json={"relatedPatientId": UNLINKED_PATIENT}, headers=auth)
    assert r.status_code == 404, r.text


def test_prescription_proceeds_without_consent(
    client: TestClient, auth: dict, db: psycopg.Connection
) -> None:
    # RATIFIED POSTURE: prescription intake is a caller-supplied document, NOT a stored-PHI
    # read, so it has NO consent gate (mirrors the paper-Rx import write). It proceeds for a
    # consent-less (but tenant-linked) patient and still records a non-break-glass purpose entry.
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_NO_CONSENT}, headers=auth)
    assert r.status_code == 200, r.text
    assert _row(db, PATIENT_NO_CONSENT) is not None  # persisted despite no consent

    with db.cursor() as cur:
        cur.execute(
            """
            SELECT is_break_glass, review_required FROM platform.purpose_of_use_log
            WHERE tenant_id = %s AND accessed_resource_id = %s
            """,
            (TENANT_ID, PATIENT_NO_CONSENT),
        )
        log = cur.fetchone()
    assert log is not None
    assert log["is_break_glass"] is False  # consent-basis record, not an override
    assert log["review_required"] is False


def test_prescription_refuses_service_token(
    client: TestClient, db: psycopg.Connection
) -> None:
    # A non-human service identity may not perform document intake (orthogonal to consent).
    headers = {
        "Authorization": f"Bearer {make_service_token()}",
        "X-Purpose-Of-Use": "treatment",
    }
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_CONSENTED}, headers=headers)
    assert r.status_code == 403, r.text
    assert _row(db, PATIENT_CONSENTED) is None  # nothing persisted


# --- Happy path -------------------------------------------------------------
def test_prescription_happy_path_generates_and_persists(
    client: TestClient, auth: dict, db: psycopg.Connection
) -> None:
    # No image/sourceUrl -> the endpoint generates the sample prescription.
    r = client.post(EXTRACT, json={"relatedPatientId": PATIENT_CONSENTED}, headers=auth)
    assert r.status_code == 200, r.text
    payload = r.json()

    assert payload["extractionId"]  # non-null
    assert payload["externalDoctorName"] == "Dr. R. Gupta"
    assert payload["recordedDate"] == "2026-05-14"
    assert payload["rawText"]
    meds = payload["records"]
    assert len(meds) >= 1
    first = meds[0]
    assert first["recordType"] == "medication"
    assert first["title"]  # a non-empty title like "Metformin 500"
    assert 0.0 <= first["confidence"] <= 1.0

    row = _row(db, PATIENT_CONSENTED)
    assert row is not None
    assert row["source_type"] == "prescription"
    assert row["requires_human_review"] is True
    assert row["status"] == "extracted"


def test_prescription_accepts_base64_image(
    client: TestClient, auth: dict, db: psycopg.Connection
) -> None:
    # Exercise the primary input path: an inline base64 photo decoded to a temp file.
    sample_path = sample_docs.generate_prescription_sample()
    with open(sample_path, "rb") as fh:
        image_b64 = base64.b64encode(fh.read()).decode("ascii")

    r = client.post(
        EXTRACT,
        json={
            "relatedPatientId": PATIENT_CONSENTED,
            "imageBase64": image_b64,
            "contentType": "image/png",
            "fileName": "rx.png",
        },
        headers=auth,
    )
    assert r.status_code == 200, r.text
    assert len(r.json()["records"]) >= 1
    assert _row(db, PATIENT_CONSENTED) is not None


def test_prescription_rejects_sourceurl_path(
    client: TestClient, auth: dict, db: psycopg.Connection, tmp_path: object
) -> None:
    # Auditor veto: no caller-supplied filesystem path. A stray sourceUrl is rejected
    # (422, extra=forbid); the file is never opened/OCR'd and nothing is persisted.
    secret = tmp_path / "not_a_prescription.txt"  # type: ignore[operator]
    secret.write_text("TOP-SECRET-LOCAL-FILE-CONTENTS")
    r = client.post(
        EXTRACT,
        json={"relatedPatientId": PATIENT_CONSENTED, "sourceUrl": str(secret)},
        headers=auth,
    )
    assert r.status_code == 422, r.text
    assert "TOP-SECRET" not in r.text  # contents never read back
    assert _row(db, PATIENT_CONSENTED) is None  # nothing persisted


def test_prescription_rejects_bad_base64(client: TestClient, auth: dict) -> None:
    r = client.post(
        EXTRACT,
        json={"relatedPatientId": PATIENT_CONSENTED, "imageBase64": "!!!not-base64!!!"},
        headers=auth,
    )
    assert r.status_code == 400, r.text


# --- Pure parser unit tests (no DB, no OCR) ---------------------------------
def test_parse_medication_name_and_strength() -> None:
    rec = prescription_parser.parse_medication_line("1. Metformin 500 mg 1-0-1 after food x 30 days")
    assert rec is not None
    assert rec["title"] == "Metformin 500"
    assert rec["description"] == "1-0-1 · after food · 30 days"
    assert rec["recordType"] == "medication"
    assert rec["confidence"] >= 0.7


def test_parse_dose_grid_variants() -> None:
    assert prescription_parser.parse_medication_line("Amlodipine 5 mg 0-0-1")["description"] == "0-0-1"
    # OCR-spaced dashes are normalised.
    rec = prescription_parser.parse_medication_line("Telmisartan 40 mg 1 - 0 - 1")
    assert rec["description"] == "1-0-1"


def test_parse_frequency_tokens() -> None:
    rec = prescription_parser.parse_medication_line("Pantoprazole 40 mg OD before food x 15 days")
    assert rec["title"] == "Pantoprazole 40"
    assert rec["description"] == "OD · before food · 15 days"


def test_parse_duration_days_weeks_months() -> None:
    assert prescription_parser.parse_medication_line("Azithromycin 500 mg OD x 3 days")["description"].endswith("3 days")
    assert prescription_parser.parse_medication_line("Ferrous 100 mg OD for 2 weeks")["description"].endswith("2 weeks")
    assert prescription_parser.parse_medication_line("Vitamin 1000 iu OD x 1 month")["description"].endswith("1 month")


def test_parse_doctor_line() -> None:
    assert prescription_parser.parse_doctor_line("Dr. R. Gupta, MBBS, MD") == "Dr. R. Gupta"
    assert prescription_parser.parse_doctor_line("Consultant: Dr Anita Rao MD") == "Dr. Anita Rao"
    assert prescription_parser.parse_doctor_line("No prescriber here") is None


def test_parse_date_formats() -> None:
    assert prescription_parser.parse_date("Date: 14/05/2026") == "2026-05-14"
    assert prescription_parser.parse_date("Written 2026-05-14 by clinic") == "2026-05-14"
    assert prescription_parser.parse_date("14-05-26") == "2026-05-14"
    assert prescription_parser.parse_date("no date at all") is None
    assert prescription_parser.parse_date("99/99/2026") is None  # not a real calendar date


def test_parser_never_throws_on_garbage() -> None:
    # Handwriting is lossy: unparseable lines return None / empty, never raise.
    garbage = "###\n@@@ scribble ~~~\n\n   \nzz"
    out = prescription_parser.parse_prescription(garbage)
    assert out["records"] == []
    assert out["external_doctor_name"] is None
    assert out["recorded_date"] is None
    assert prescription_parser.parse_medication_line("scribble ~~~") is None
    assert prescription_parser.parse_medication_line("") is None


def test_prose_lines_are_not_medications() -> None:
    # A name with no clinical signal (no strength/dose/duration) is not a drug row.
    assert prescription_parser.parse_medication_line("Patient reports feeling better") is None
    assert prescription_parser.parse_medication_line("Advised rest and fluids") is None
