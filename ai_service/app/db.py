"""Database access helpers.

A thin context-managed connection helper over psycopg 3. The connection uses the
owner role (dev choice) which bypasses RLS — therefore EVERY query in this service
MUST explicitly filter `tenant_id = <jwt tenant>`. Tenant isolation lives in code.
"""
from __future__ import annotations

from contextlib import contextmanager
from typing import Iterator

import psycopg
from psycopg.rows import dict_row

from .config import get_settings


@contextmanager
def get_connection() -> Iterator[psycopg.Connection]:
    """Yield a short-lived autocommit-off connection with dict rows.

    Commits on clean exit, rolls back on exception, always closes.
    """
    settings = get_settings()
    conn = psycopg.connect(settings.database_url, row_factory=dict_row)
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def check_db_connection() -> bool:
    """Best-effort connectivity probe for the health endpoint."""
    try:
        with get_connection() as conn:
            with conn.cursor() as cur:
                cur.execute("SELECT 1 AS ok")
                row = cur.fetchone()
                return bool(row and row["ok"] == 1)
    except Exception:
        return False
