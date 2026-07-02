"""Synthetic lab-report image generator (the OCR input).

Renders a clean, high-resolution CBC (Complete Blood Count) panel PNG with a
monospaced font so Tesseract reads it reliably. The generated file is the input
to the OCR extraction workflow.
"""
from __future__ import annotations

import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from .config import get_settings

# A CBC panel with at least three abnormal analytes to exercise flagging.
# (label, result, unit, ref_low, ref_high)
CBC_PANEL: list[tuple[str, str, str, str, str]] = [
    ("Hemoglobin", "9.8", "g/dL", "13.0", "17.0"),    # LOW
    ("WBC", "11.2", "10^3/uL", "4.0", "11.0"),        # HIGH
    ("Platelets", "250", "10^3/uL", "150", "410"),    # normal
    ("RBC", "4.6", "10^6/uL", "4.5", "5.5"),          # normal
    ("Hematocrit", "38", "%", "40", "50"),            # LOW
    ("MCV", "84", "fL", "80", "100"),                 # normal
]


def _load_mono_font(size: int) -> ImageFont.FreeTypeFont:
    """Load a monospaced TrueType font; Tesseract is most accurate on these."""
    candidates = [
        "/System/Library/Fonts/Supplemental/Andale Mono.ttf",
        "/System/Library/Fonts/Supplemental/Courier New.ttf",
        "/System/Library/Fonts/Monaco.ttf",
        "/Library/Fonts/Arial.ttf",
    ]
    for path in candidates:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def generate_cbc_report(
    *,
    patient_name: str = "Riya Sharma",
    report_date: str = "2026-06-16",
    lab_name: str = "Apollo Diagnostics",
    out_path: str | None = None,
) -> str:
    """Render a CBC lab-report PNG and return its absolute path."""
    settings = get_settings()
    samples_dir = Path(settings.samples_dir)
    samples_dir.mkdir(parents=True, exist_ok=True)
    target = Path(out_path) if out_path else samples_dir / "cbc_lab_report.png"

    # High resolution + generous spacing => clean OCR.
    width, height = 1400, 1000
    margin = 70
    black = (0, 0, 0)
    img = Image.new("RGB", (width, height), color=(255, 255, 255))
    draw = ImageDraw.Draw(img)

    title_font = _load_mono_font(46)
    head_font = _load_mono_font(30)
    body_font = _load_mono_font(30)

    y = margin
    draw.text((margin, y), lab_name, font=title_font, fill=black)
    y += 70
    draw.text((margin, y), "COMPLETE BLOOD COUNT (CBC)", font=head_font, fill=black)
    y += 55
    draw.text((margin, y), f"Patient: {patient_name}", font=body_font, fill=black)
    y += 42
    draw.text((margin, y), f"Report Date: {report_date}", font=body_font, fill=black)
    y += 60

    # Horizontal rule.
    draw.line([(margin, y), (width - margin, y)], fill=black, width=2)
    y += 25

    # Column layout (monospaced => align by pixel x).
    col_test, col_result, col_unit, col_ref = margin, margin + 430, margin + 660, margin + 900
    draw.text((col_test, y), "Test", font=head_font, fill=black)
    draw.text((col_result, y), "Result", font=head_font, fill=black)
    draw.text((col_unit, y), "Unit", font=head_font, fill=black)
    draw.text((col_ref, y), "Reference Range", font=head_font, fill=black)
    y += 48
    draw.line([(margin, y), (width - margin, y)], fill=black, width=2)
    y += 25

    for label, result, unit, ref_low, ref_high in CBC_PANEL:
        draw.text((col_test, y), label, font=body_font, fill=black)
        draw.text((col_result, y), result, font=body_font, fill=black)
        draw.text((col_unit, y), unit, font=body_font, fill=black)
        draw.text((col_ref, y), f"{ref_low} - {ref_high}", font=body_font, fill=black)
        y += 52

    img.save(target, "PNG", dpi=(300, 300))
    return str(target.resolve())


# A handwritten-style prescription. Each row: (drug, strength, dose, food, duration).
# Rendered in a clean font (like the CBC sample) so Tesseract reads it reliably — a
# real handwriting scan is lossy on purpose, but the demo/happy-path must parse.
RX_LINES: list[tuple[str, str, str, str, str]] = [
    ("Metformin", "500 mg", "1-0-1", "after food", "x 30 days"),
    ("Amlodipine", "5 mg", "1-0-0", "", "x 1 month"),
    ("Atorvastatin", "10 mg", "0-0-1", "HS", "x 30 days"),
    ("Pantoprazole", "40 mg", "OD", "before food", "x 15 days"),
]


def generate_prescription_sample(
    *,
    doctor_name: str = "Dr. R. Gupta",
    clinic_name: str = "Apollo Clinic",
    patient_name: str = "Riya Sharma",
    prescribed_date: str = "14/05/2026",
    out_path: str | None = None,
) -> str:
    """Render a synthetic paper-prescription PNG and return its absolute path.

    Mirrors generate_cbc_report: high-resolution, generously spaced, clean font so
    the OCR + prescription parser have a deterministic happy path for demos/tests.
    """
    settings = get_settings()
    samples_dir = Path(settings.samples_dir)
    samples_dir.mkdir(parents=True, exist_ok=True)
    target = Path(out_path) if out_path else samples_dir / "paper_prescription.png"

    width, height = 1400, 1000
    margin = 70
    black = (0, 0, 0)
    img = Image.new("RGB", (width, height), color=(255, 255, 255))
    draw = ImageDraw.Draw(img)

    title_font = _load_mono_font(44)
    head_font = _load_mono_font(30)
    body_font = _load_mono_font(30)

    y = margin
    draw.text((margin, y), f"{doctor_name}, MBBS, MD", font=title_font, fill=black)
    y += 60
    draw.text((margin, y), clinic_name, font=head_font, fill=black)
    y += 50
    draw.text((margin, y), f"Date: {prescribed_date}", font=body_font, fill=black)
    y += 45
    draw.text((margin, y), f"Patient: {patient_name}", font=body_font, fill=black)
    y += 60

    draw.line([(margin, y), (width - margin, y)], fill=black, width=2)
    y += 25

    # The big "Rx" superscription every prescription opens with.
    draw.text((margin, y), "Rx", font=title_font, fill=black)
    y += 75

    for idx, (drug, strength, dose, food, duration) in enumerate(RX_LINES, start=1):
        parts = [f"{idx}. {drug} {strength}", dose]
        if food:
            parts.append(food)
        parts.append(duration)
        draw.text((margin + 30, y), "   ".join(parts), font=body_font, fill=black)
        y += 58

    img.save(target, "PNG", dpi=(300, 300))
    return str(target.resolve())


if __name__ == "__main__":
    print(generate_cbc_report())
    print(generate_prescription_sample())
