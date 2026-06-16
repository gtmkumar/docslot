#!/usr/bin/env python3
"""
Regenerates database/docslot_complete.sql from the canonical numbered SQL files.

The bundle is the 9 canonical files concatenated in dependency order, each wrapped
in a "PART N/9" banner + RAISE NOTICE start/end markers. Source files remain
canonical; this script keeps the bundle a faithful, deterministic concatenation.

Run after editing any canonical file:
    python3 database/regenerate_bundle.py
"""
import pathlib

HERE = pathlib.Path(__file__).resolve().parent

# (source file, part title, part description) — in canonical execution order.
PARTS = [
    ("01_platform_core.sql",     "Platform Core",            "Identity, RBAC, audit, billing, tenant model — platform.*"),
    ("02_platform_api.sql",      "Platform API",             "OAuth 2.0, scoped JWT tokens, webhooks — platform_api.*"),
    ("03_docslot.sql",           "DocSlot Product",          "Booking, prescriptions, ABDM, WhatsApp — docslot.*"),
    ("05_security_hardening.sql","Security Hardening",       "Encryption, audit chain, RLS, anomaly detection — platform.*"),
    ("06_ai_services.sql",       "AI Services",              "LangGraph, embeddings, OCR, predictions — ai.*"),
    ("07_commission_broker.sql", "Commission & Broker",      "Broker referrals, commission, attribution, payouts — commission.*"),
    ("08_rbac_navigation.sql",   "RBAC Enhancements",        "Backend-driven menus, overrides, fast permission resolver — platform.*"),
    ("09_chat_identity.sql",     "Chat Identity & Discount", "WA contact memory, behalf-booking consent, direct discount — docslot.*"),
    ("04_future_products.sql",   "Future Products (Optional)","RuralReach + SafeHer + GenericFirst"),
    ("10_roles_grants.sql",      "Roles & Grants",           "Least-privilege docslot_app role + grants (RLS-enforced, audit append-only) — runs LAST"),
]

HEADER = """\
-- ============================================================================
-- DocSlot Platform — Complete Schema Bundle (All-in-One)
-- ============================================================================
-- This is the bundled equivalent of running the 9 canonical files in order:
--   01_platform_core.sql -> 02_platform_api.sql -> 03_docslot.sql
--   -> 05_security_hardening.sql -> 06_ai_services.sql -> 07_commission_broker.sql
--   -> 08_rbac_navigation.sql -> 09_chat_identity.sql -> 04_future_products.sql
--
-- Source files remain canonical in database/*.sql. This bundle is REGENERATED
-- from them by database/regenerate_bundle.py — if you change a source file, re-run it.
--
-- USAGE
--   createdb docslot_platform
--   psql -d docslot_platform -f docslot_complete.sql
--
-- NOT IDEMPOTENT — designed for a fresh, empty database. To re-run: drop the DB first.
-- ============================================================================

\\set ON_ERROR_STOP on

DO $bundle$
BEGIN
    RAISE NOTICE '';
    RAISE NOTICE 'DocSlot Platform Schema Bundle - Starting installation';
    RAISE NOTICE '';
END $bundle$;

"""

FOOTER = """\

-- ============================================================================
-- END OF BUNDLE
-- ============================================================================
"""

def banner(idx, total, title, desc, src):
    return (
        "\n\n"
        "-- ============================================================================\n"
        f"-- PART {idx}/{total}: {title}\n"
        f"-- {desc}\n"
        f"-- Source: database/{src}\n"
        "-- ============================================================================\n\n"
        "DO $section$\n"
        "BEGIN\n"
        f"    RAISE NOTICE '--- PART {idx}/{total}: {title}: % ---', '{src}';\n"
        "END $section$;\n\n"
    )

def end_banner(idx, total, title):
    return (
        "\n\n"
        "DO $section_end$\n"
        "BEGIN\n"
        f"    RAISE NOTICE '--- PART {idx}/{total}: {title} complete ---';\n"
        "    RAISE NOTICE '';\n"
        "END $section_end$;\n"
    )

def main():
    out = [HEADER]
    total = len(PARTS)
    for i, (src, title, desc) in enumerate(PARTS, start=1):
        body = (HERE / src).read_text(encoding="utf-8")
        out.append(banner(i, total, title, desc, src))
        out.append(body.rstrip() + "\n")
        out.append(end_banner(i, total, title))
    out.append(FOOTER)
    (HERE / "docslot_complete.sql").write_text("".join(out), encoding="utf-8")
    print("Regenerated database/docslot_complete.sql from 9 canonical files.")

if __name__ == "__main__":
    main()
