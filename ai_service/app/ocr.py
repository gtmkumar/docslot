"""OCR + lab-report parsing.

Runs Tesseract over a lab-report image, computes an overall confidence from the
per-word confidences, and parses the raw text into structured analytes with
LOW/HIGH/normal flags using OCR-noise-tolerant line regexes.
"""
from __future__ import annotations

import logging
import re

import pytesseract
from PIL import Image

from .config import get_settings

logger = logging.getLogger("ai_service.ocr")

_settings = get_settings()
pytesseract.pytesseract.tesseract_cmd = _settings.tesseract_cmd


# ---------------------------------------------------------------------------
# OCR
# ---------------------------------------------------------------------------
def run_ocr(image_path: str) -> tuple[str, float]:
    """Return (raw_text, overall_confidence in 0..1).

    Confidence is the mean of per-word Tesseract confidences for real words
    (conf >= 0 and non-empty text), scaled to 0..1.
    """
    image = Image.open(image_path)
    raw_text = pytesseract.image_to_string(image)

    data = pytesseract.image_to_data(image, output_type=pytesseract.Output.DICT)
    confs: list[float] = []
    for text, conf in zip(data["text"], data["conf"]):
        if text and text.strip():
            try:
                c = float(conf)
            except (TypeError, ValueError):
                continue
            if c >= 0:
                confs.append(c)
    overall = (sum(confs) / len(confs) / 100.0) if confs else 0.0
    return raw_text, round(overall, 3)


# ---------------------------------------------------------------------------
# Parsing
# ---------------------------------------------------------------------------
_NUM = r"[-+]?\d+(?:\.\d+)?"

# A data line: <test words> <value> <unit token(s)> <low> [-/to] <high>
# Tolerant of OCR noise: en-dash/hyphen/"to" between range bounds, optional
# noise tokens for the unit. Anchored on the trailing numeric reference range.
_LINE_RE = re.compile(
    rf"""
    ^\s*
    (?P<test>[A-Za-z][A-Za-z .()/%-]*?)        # test name
    \s+
    (?P<value>{_NUM})                          # result value
    \s+
    (?P<unit>[^\s]+(?:\s+[^\s]+)?)?            # unit (1-2 tokens, optional)
    \s+
    (?P<low>{_NUM})                            # ref low
    \s*(?:-|–|—|to|/)\s*                       # range separator
    (?P<high>{_NUM})                           # ref high
    \s*$
    """,
    re.VERBOSE,
)

# Known CBC analyte names (used to clean up OCR-mangled test labels).
_KNOWN_TESTS = [
    "Hemoglobin", "WBC", "Platelets", "RBC", "Hematocrit", "MCV",
    "MCH", "MCHC", "RDW", "Neutrophils", "Lymphocytes",
]

# Tokens that are clearly headers, not analytes.
_HEADER_TOKENS = {"test", "result", "unit", "reference", "range"}


def _canonical_test(name: str) -> str:
    cleaned = name.strip().strip(".:|").strip()
    low = cleaned.lower()
    for known in _KNOWN_TESTS:
        if known.lower() == low or known.lower() in low:
            return known
    return cleaned


def _flag(value: float, low: float, high: float) -> str:
    if value < low:
        return "low"
    if value > high:
        return "high"
    return "normal"


def parse_analytes(raw_text: str) -> list[dict]:
    """Parse raw OCR text into structured analyte dicts.

    Each: {test, value, unit, refLow, refHigh, flag}.
    """
    analytes: list[dict] = []
    seen: set[str] = set()
    for line in raw_text.splitlines():
        if not line.strip():
            continue
        # Skip obvious header rows.
        if line.strip().lower().split()[:1] and line.strip().lower().split()[0] in _HEADER_TOKENS:
            continue
        m = _LINE_RE.match(line)
        if not m:
            continue
        try:
            value = float(m.group("value"))
            low = float(m.group("low"))
            high = float(m.group("high"))
        except ValueError:
            continue
        test = _canonical_test(m.group("test"))
        if not test or test.lower() in _HEADER_TOKENS:
            continue
        key = test.lower()
        if key in seen:
            continue
        seen.add(key)
        unit = (m.group("unit") or "").strip() or None
        analytes.append(
            {
                "test": test,
                "value": value,
                "unit": unit,
                "refLow": low,
                "refHigh": high,
                "flag": _flag(value, low, high),
            }
        )
    return analytes


def extract_lab_report(image_path: str) -> dict:
    """Full pipeline: OCR + parse. Returns the assembled extraction payload."""
    raw_text, confidence = run_ocr(image_path)
    analytes = parse_analytes(raw_text)
    abnormal = [a for a in analytes if a["flag"] != "normal"]
    return {
        "raw_text": raw_text,
        "overall_confidence": confidence,
        "analytes": analytes,
        "abnormal_count": len(abnormal),
        "panel": "CBC",
    }
