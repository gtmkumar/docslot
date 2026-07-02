"""Best-effort prescription parser (PURE, unit-testable — no DB, no OCR engine).

Turns the raw OCR text of a (typically handwritten) prescription into structured
medication records plus a prescriber line and a written date. Handwriting OCR is
LOSSY by nature: this module parses what is parseable and NEVER throws on a line
it cannot understand — unrecognised text survives only in the caller's rawText.

The output shape mirrors the fixed AI-service contract consumed by the .NET proxy
and the React intake UI:

    {
      "records": [ {recordType, title, description|None, confidence}, ... ],
      "external_doctor_name": str | None,   # "Dr. R. Gupta"
      "recorded_date": str | None,          # ISO "2026-05-14"
    }

Every record is recordType='medication' (this parser only recognises drug lines);
titles look like "Metformin 500" (name + strength value), descriptions fold the
dose grid / frequency, food timing and duration into one human-readable line.
"""
from __future__ import annotations

import re
from datetime import date

# --- Medication-line building blocks ---------------------------------------
# A strength: a number optionally followed by a common unit. We keep the numeric
# value in the title ("Metformin 500") and drop the unit there (the UI shows it
# from the review form); the unit is tolerated so it is not mis-read as a name.
_STRENGTH_RE = re.compile(
    r"(?P<value>\d+(?:\.\d+)?)\s*(?P<unit>mcg|mg|ml|gm|g|iu|units?|%)?\b",
    re.IGNORECASE,
)

# A dose grid: 1-0-1, 0-0-1, 1-1-1, or a 4-slot 1-0-1-0 (OCR may space the dashes).
_GRID_RE = re.compile(r"\b\d\s*[-–]\s*\d\s*[-–]\s*\d(?:\s*[-–]\s*\d)?\b")

# Latin frequency abbreviations a prescriber writes instead of a grid.
_FREQ_TOKENS = ("OD", "BD", "BID", "TDS", "TID", "QID", "QDS", "HS", "QHS", "SOS", "PRN")
_FREQ_RE = re.compile(r"\b(" + "|".join(_FREQ_TOKENS) + r")\b")

# Food timing (long forms + the a/f b/f p/c a/c shorthand doctors scrawl).
_FOOD_RE = re.compile(
    r"\b(after\s+food|before\s+food|with\s+food|empty\s+stomach|a/?f|b/?f|p/?c|a/?c)\b",
    re.IGNORECASE,
)

# Duration: "x 5 days", "x5 days", "for 2 weeks", "1 month", "10 days".
_DURATION_RE = re.compile(
    r"(?:x|for)?\s*(?P<n>\d+)\s*(?P<unit>days?|weeks?|months?|/?7)\b",
    re.IGNORECASE,
)

# Lines that are clearly NOT medication rows (prescriber, patient, header, date).
_NON_MED_PREFIX = re.compile(
    r"^\s*(dr\b|patient\b|name\b|age\b|date\b|rx\b|clinic\b|hospital\b|"
    r"reg\b|regd\b|address\b|ph\b|phone\b|mob\b|tel\b)",
    re.IGNORECASE,
)

# A leading list marker: "1.", "2)", "-", "•".
_LIST_MARKER = re.compile(r"^\s*(?:\d+\s*[.)-]|[-•*])\s*")

# A plausible drug-name token: alphabetic, >= 3 chars (drops stray OCR letters).
_NAME_TOKEN = re.compile(r"^[A-Za-z][A-Za-z'-]{2,}$")


def _clean_name(fragment: str) -> str | None:
    """Reduce a pre-strength fragment to a plausible drug name, or None."""
    fragment = _LIST_MARKER.sub("", fragment).strip(" .:-\t")
    tokens = [t for t in re.split(r"\s+", fragment) if t]
    keep: list[str] = []
    for tok in tokens:
        if _NAME_TOKEN.match(tok):
            keep.append(tok)
        elif keep:
            break  # stop at the first non-name token once a name has started
    if not keep:
        return None
    name = " ".join(keep)
    # Title-case a single lowercase word (OCR sometimes lowercases); leave mixed as-is.
    return name[:1].upper() + name[1:]


def _extract_dose(line: str) -> str | None:
    """The dose signal: a numeric grid (preferred) or a Latin frequency token."""
    grid = _GRID_RE.search(line)
    if grid:
        return re.sub(r"\s*[-–]\s*", "-", grid.group(0)).replace("–", "-")
    freq = _FREQ_RE.search(line.upper())
    if freq:
        return freq.group(1)
    return None


def _extract_duration(line: str) -> str | None:
    m = _DURATION_RE.search(line)
    if not m:
        return None
    unit = m.group("unit").lower()
    if unit in ("/7", "7"):
        unit = "days"
    return f"{m.group('n')} {unit}"


def _extract_food(line: str) -> str | None:
    m = _FOOD_RE.search(line)
    if not m:
        return None
    token = m.group(1).lower().replace("/", "")
    mapping = {
        "af": "after food",
        "bf": "before food",
        "pc": "after food",
        "ac": "before food",
    }
    return mapping.get(token, re.sub(r"\s+", " ", m.group(1).lower()))


def parse_medication_line(line: str) -> dict[str, object] | None:
    """Parse ONE line into a medication record dict, or None if it is not a drug row.

    Record: {recordType:'medication', title, description|None, confidence:0..1}.
    Never raises — an unparseable line simply returns None.
    """
    stripped = line.strip()
    if not stripped or _NON_MED_PREFIX.match(stripped):
        return None

    body = _LIST_MARKER.sub("", stripped)
    strength = _STRENGTH_RE.search(body)
    dose = _extract_dose(body)
    duration = _extract_duration(body)
    food = _extract_food(body)

    # Name is the text before the strength (or before the first dose/duration signal).
    cut = len(body)
    if strength:
        cut = min(cut, strength.start())
    name = _clean_name(body[:cut]) or _clean_name(body)
    if name is None:
        return None

    # A drug row needs a name AND at least one clinical signal, else it is prose.
    if not (strength or dose or duration):
        return None

    strength_value = strength.group("value") if strength else None
    title = f"{name} {strength_value}" if strength_value else name

    parts = [p for p in (dose, food, duration) if p]
    description = " · ".join(parts) if parts else None

    confidence = 0.35
    if strength:
        confidence += 0.25
    if dose:
        confidence += 0.25
    if duration:
        confidence += 0.10
    if food:
        confidence += 0.05
    confidence = round(min(confidence, 0.95), 2)

    return {
        "recordType": "medication",
        "title": title,
        "description": description,
        "confidence": confidence,
    }


# --- Prescriber + date ------------------------------------------------------
_DOCTOR_RE = re.compile(
    r"\bDr\.?\s*(?P<name>[A-Z][A-Za-z.\s]*?)(?=,|\bMBBS\b|\bMD\b|\bMS\b|\bDNB\b|$|\n)",
    re.IGNORECASE,
)


def parse_doctor_line(raw_text: str) -> str | None:
    """Extract a normalised prescriber line ("Dr. R. Gupta") or None."""
    m = _DOCTOR_RE.search(raw_text)
    if not m:
        return None
    name = re.sub(r"\s+", " ", m.group("name")).strip(" .,")
    if not name:
        return None
    return f"Dr. {name}"


# dd/mm/yyyy or dd-mm-yyyy (Indian convention) OR yyyy-mm-dd (ISO).
_DMY_RE = re.compile(r"\b(?P<d>\d{1,2})[/-](?P<m>\d{1,2})[/-](?P<y>\d{2,4})\b")
_ISO_RE = re.compile(r"\b(?P<y>\d{4})-(?P<m>\d{1,2})-(?P<d>\d{1,2})\b")


def parse_date(raw_text: str) -> str | None:
    """Extract the written date as an ISO string ("2026-05-14"), or None.

    Prefers an unambiguous ISO date; falls back to dd/mm/yyyy (Indian order).
    Returns None on any value that is not a real calendar date.
    """
    iso = _ISO_RE.search(raw_text)
    if iso:
        parsed = _safe_date(int(iso.group("y")), int(iso.group("m")), int(iso.group("d")))
        if parsed:
            return parsed
    dmy = _DMY_RE.search(raw_text)
    if dmy:
        year = int(dmy.group("y"))
        if year < 100:
            year += 2000
        parsed = _safe_date(year, int(dmy.group("m")), int(dmy.group("d")))
        if parsed:
            return parsed
    return None


def _safe_date(year: int, month: int, day: int) -> str | None:
    try:
        return date(year, month, day).isoformat()
    except ValueError:
        return None


def parse_prescription(raw_text: str) -> dict[str, object]:
    """Full best-effort parse of raw prescription OCR text (PURE).

    Returns {records, external_doctor_name, recorded_date}. Lines that parse into
    a medication become records; everything else is ignored here (it survives in
    the caller's rawText). Never raises.
    """
    records: list[dict[str, object]] = []
    seen: set[str] = set()
    for line in raw_text.splitlines():
        record = parse_medication_line(line)
        if record is None:
            continue
        key = str(record["title"]).lower()
        if key in seen:
            continue
        seen.add(key)
        records.append(record)

    return {
        "records": records,
        "external_doctor_name": parse_doctor_line(raw_text),
        "recorded_date": parse_date(raw_text),
    }
