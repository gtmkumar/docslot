"""Data access for the agentic triage-routing workflow.

TENANT ISOLATION (this service uses the owner connection, which BYPASSES RLS):
  - EVERY query here filters `tenant_id = <jwt tenant>`. The agentic tables
    (ai.ai_workflows / ai.ai_agent_runs / ai.ai_agent_steps) and the docslot
    lookups (departments / doctors / time_slots) are all tenant-scoped in code.
  - Patient / booking references are PHI; those rows are verified against the
    JWT tenant before any work proceeds (cross-tenant => not found).

The workflow row itself is GLOBAL (tenant_id NULL) — it is a reusable graph
definition, not tenant data — but every RUN it produces carries the caller's
tenant_id, and every step belongs to a tenant-scoped run.
"""
from __future__ import annotations

import json
import logging

from .db import get_connection

logger = logging.getLogger("ai_service.triage_repository")

WORKFLOW_KEY = "triage_routing"
WORKFLOW_NAME = "Symptom Triage & Department Routing"
WORKFLOW_USE_CASE = "triage"


# ---------------------------------------------------------------------------
# Workflow registry (global graph definition — idempotent upsert)
# ---------------------------------------------------------------------------
def upsert_workflow(graph_definition: dict) -> str:
    """Create or refresh the global triage_routing workflow row; return its id.

    The unique key is (workflow_key, version, tenant_id). We pin version=1 and
    tenant_id IS NULL (a shared graph). Re-running keeps the same workflow_id.
    """
    with get_connection() as conn, conn.cursor() as cur:
        # The unique constraint involves a NULL column, so ON CONFLICT can't
        # target it portably; do a manual upsert keyed on (key, version, NULL).
        cur.execute(
            """
            SELECT workflow_id FROM ai.ai_workflows
            WHERE workflow_key = %(key)s AND version = 1 AND tenant_id IS NULL
            LIMIT 1
            """,
            {"key": WORKFLOW_KEY},
        )
        row = cur.fetchone()
        if row:
            cur.execute(
                """
                UPDATE ai.ai_workflows
                SET name = %(name)s, use_case = %(use_case)s,
                    graph_definition = %(graph)s, is_active = true,
                    activated_at = COALESCE(activated_at, NOW())
                WHERE workflow_id = %(id)s
                """,
                {
                    "name": WORKFLOW_NAME,
                    "use_case": WORKFLOW_USE_CASE,
                    "graph": json.dumps(graph_definition),
                    "id": row["workflow_id"],
                },
            )
            return str(row["workflow_id"])

        cur.execute(
            """
            INSERT INTO ai.ai_workflows (
                workflow_key, tenant_id, name, description, version, use_case,
                graph_definition, is_active, activated_at
            ) VALUES (
                %(key)s, NULL, %(name)s, %(desc)s, 1, %(use_case)s,
                %(graph)s, true, NOW()
            )
            RETURNING workflow_id
            """,
            {
                "key": WORKFLOW_KEY,
                "name": WORKFLOW_NAME,
                "desc": "Offline rule-based 4-node triage: extract symptoms -> "
                "route department -> score urgency -> suggest doctors.",
                "use_case": WORKFLOW_USE_CASE,
                "graph": json.dumps(graph_definition),
            },
        )
        return str(cur.fetchone()["workflow_id"])


# ---------------------------------------------------------------------------
# Agent runs (tenant-scoped)
# ---------------------------------------------------------------------------
def create_run(
    *,
    workflow_id: str,
    tenant_id: str,
    user_id: str,
    input_data: dict,
    related_resource_type: str | None,
    related_resource_id: str | None,
) -> str:
    """Insert a 'running' run row for this tenant; return run_id."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO ai.ai_agent_runs (
                workflow_id, tenant_id, triggered_by_user_id, triggered_by_event,
                related_resource_type, related_resource_id, input_data, status,
                started_at
            ) VALUES (
                %(workflow_id)s, %(tenant_id)s, %(user_id)s, 'api.triage',
                %(rrt)s, %(rri)s, %(input)s, 'running', NOW()
            )
            RETURNING run_id
            """,
            {
                "workflow_id": workflow_id,
                "tenant_id": tenant_id,
                "user_id": user_id,
                "rrt": related_resource_type,
                "rri": related_resource_id,
                "input": json.dumps(input_data),
            },
        )
        return str(cur.fetchone()["run_id"])


def finalize_run_success(
    *,
    run_id: str,
    tenant_id: str,
    output_data: dict,
    duration_ms: int,
    iterations_used: int,
) -> None:
    """Mark the run 'success' with its output + latency (tenant-scoped update)."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            UPDATE ai.ai_agent_runs
            SET status = 'success', output_data = %(output)s,
                completed_at = NOW(), duration_ms = %(dur)s,
                iterations_used = %(iters)s
            WHERE run_id = %(run_id)s AND tenant_id = %(tenant_id)s
            """,
            {
                "output": json.dumps(output_data),
                "dur": duration_ms,
                "iters": iterations_used,
                "run_id": run_id,
                "tenant_id": tenant_id,
            },
        )


def finalize_run_failed(
    *, run_id: str, tenant_id: str, node: str, message: str
) -> None:
    """Mark the run 'failed' (tenant-scoped). Best-effort — never raises."""
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                UPDATE ai.ai_agent_runs
                SET status = 'failed', completed_at = NOW(),
                    failed_at_node = %(node)s, error_code = 'node_error',
                    error_message = %(msg)s
                WHERE run_id = %(run_id)s AND tenant_id = %(tenant_id)s
                """,
                {"node": node, "msg": message, "run_id": run_id, "tenant_id": tenant_id},
            )
    except Exception as exc:  # noqa: BLE001
        logger.warning("finalize_run_failed write failed (continuing): %s", exc)


def insert_step(
    *,
    run_id: str,
    step_number: int,
    node_name: str,
    step_type: str,
    tool_name: str,
    tool_input: dict,
    tool_output: dict,
    duration_ms: int,
    model_used: str | None = None,
) -> None:
    """Record ONE node execution as an ai.ai_agent_steps row.

    step_type is CHECK-constrained to llm_call|tool_call|condition|human_input|
    parallel|aggregator. Rule nodes use 'tool_call'; the optional LLM symptom
    path uses 'llm_call'.
    """
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            INSERT INTO ai.ai_agent_steps (
                run_id, step_number, node_name, step_type, model_used,
                tool_name, tool_input, tool_output, started_at, completed_at,
                duration_ms, success, state_snapshot
            ) VALUES (
                %(run_id)s, %(step_number)s, %(node_name)s, %(step_type)s, %(model)s,
                %(tool_name)s, %(tool_input)s, %(tool_output)s, NOW(), NOW(),
                %(dur)s, true, %(snapshot)s
            )
            """,
            {
                "run_id": run_id,
                "step_number": step_number,
                "node_name": node_name,
                "step_type": step_type,
                "model": model_used,
                "tool_name": tool_name,
                "tool_input": json.dumps(tool_input),
                "tool_output": json.dumps(tool_output),
                "dur": duration_ms,
                "snapshot": json.dumps(tool_output),
            },
        )


def list_runs(tenant_id: str, limit: int) -> list[dict]:
    """The tenant's recent triage runs (newest first)."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT run_id, status, started_at, output_data
            FROM ai.ai_agent_runs
            WHERE tenant_id = %(tenant_id)s
            ORDER BY started_at DESC
            LIMIT %(limit)s
            """,
            {"tenant_id": tenant_id, "limit": limit},
        )
        return list(cur.fetchall())


def get_run(tenant_id: str, run_id: str) -> dict | None:
    """One run + its ordered steps, tenant-scoped. None if not found in tenant."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT run_id, workflow_id, status, input_data, output_data,
                   started_at, completed_at, duration_ms
            FROM ai.ai_agent_runs
            WHERE run_id = %(run_id)s AND tenant_id = %(tenant_id)s
            LIMIT 1
            """,
            {"run_id": run_id, "tenant_id": tenant_id},
        )
        run = cur.fetchone()
        if run is None:
            return None

        cur.execute(
            """
            SELECT step_number, node_name, step_type, success, duration_ms,
                   tool_input, tool_output
            FROM ai.ai_agent_steps
            WHERE run_id = %(run_id)s
            ORDER BY step_number
            """,
            {"run_id": run_id},
        )
        run["steps"] = list(cur.fetchall())
        return run


# ---------------------------------------------------------------------------
# Tenant-scoped clinical lookups (departments / doctors / slots)
# ---------------------------------------------------------------------------
def list_departments(tenant_id: str) -> list[str]:
    """Active department names for the tenant."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT name FROM docslot.departments
            WHERE tenant_id = %(tenant_id)s AND is_active = true
            ORDER BY name
            """,
            {"tenant_id": tenant_id},
        )
        return [r["name"] for r in cur.fetchall()]


def suggest_doctors(tenant_id: str, department_name: str, limit: int = 3) -> list[dict]:
    """Up to `limit` active doctors in the routed department for this tenant,
    each with their earliest available upcoming slot.

    Prefers doctors accepting new patients; if none in the department are
    accepting, falls back to active doctors so the department is never empty.
    """
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT d.doctor_id, d.full_name, d.specialization, d.consultation_fee,
                   d.is_accepting_new_patients,
                   (
                       SELECT to_char(ts.slot_date, 'YYYY-MM-DD') || ' ' ||
                              to_char(ts.start_time, 'HH24:MI')
                       FROM docslot.time_slots ts
                       WHERE ts.doctor_id = d.doctor_id
                         AND ts.tenant_id = %(tenant_id)s
                         AND ts.status = 'available'
                         AND ts.slot_date >= CURRENT_DATE
                       ORDER BY ts.slot_date, ts.start_time
                       LIMIT 1
                   ) AS next_available_slot
            FROM docslot.doctors d
            JOIN docslot.departments dep ON dep.department_id = d.department_id
            WHERE d.tenant_id = %(tenant_id)s
              AND dep.tenant_id = %(tenant_id)s
              AND dep.name = %(dept)s
              AND d.is_active = true
              AND d.deleted_at IS NULL
            ORDER BY d.is_accepting_new_patients DESC, d.consultation_fee ASC NULLS LAST,
                     d.full_name
            LIMIT %(limit)s
            """,
            {"tenant_id": tenant_id, "dept": department_name, "limit": limit},
        )
        rows = list(cur.fetchall())
    out: list[dict] = []
    for r in rows:
        out.append(
            {
                "doctorId": str(r["doctor_id"]),
                "fullName": r["full_name"],
                "specialization": r["specialization"],
                "consultationFee": float(r["consultation_fee"])
                if r["consultation_fee"] is not None
                else None,
                "nextAvailableSlot": r["next_available_slot"],
            }
        )
    return out


# ---------------------------------------------------------------------------
# PHI scoping (patient / booking belong to the JWT tenant?)
# ---------------------------------------------------------------------------
def patient_linked_to_tenant(tenant_id: str, patient_id: str) -> bool:
    """True iff the patient is linked to this tenant via patient_tenant_links."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT 1 FROM docslot.patient_tenant_links
            WHERE tenant_id = %(tenant_id)s AND patient_id = %(patient_id)s
            LIMIT 1
            """,
            {"tenant_id": tenant_id, "patient_id": patient_id},
        )
        return cur.fetchone() is not None


def booking_in_tenant(tenant_id: str, booking_id: str) -> bool:
    """True iff the booking belongs to this tenant."""
    with get_connection() as conn, conn.cursor() as cur:
        cur.execute(
            """
            SELECT 1 FROM docslot.bookings
            WHERE tenant_id = %(tenant_id)s AND booking_id = %(booking_id)s
            LIMIT 1
            """,
            {"tenant_id": tenant_id, "booking_id": booking_id},
        )
        return cur.fetchone() is not None


# ---------------------------------------------------------------------------
# Compliance logging (best-effort, mirrors rag_repository)
# ---------------------------------------------------------------------------
def log_purpose_of_use_best_effort(
    *, user_id: str, tenant_id: str, resource_type: str, resource_id: str, purpose: str
) -> None:
    """Best-effort write to platform.purpose_of_use_log for a PHI access."""
    allowed = {
        "treatment", "follow_up", "emergency", "consultation",
        "research", "audit", "patient_request", "legal_obligation",
    }
    declared = purpose if purpose in allowed else "treatment"
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.purpose_of_use_log (
                    user_id, tenant_id, accessed_resource_type,
                    accessed_resource_id, declared_purpose, purpose_notes
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, %(rtype)s,
                    %(rid)s, %(declared)s, %(notes)s
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "rtype": resource_type,
                    "rid": resource_id,
                    "declared": declared,
                    "notes": f"AI triage access (raw purpose header: {purpose})",
                },
            )
    except Exception as exc:  # noqa: BLE001
        logger.warning("purpose_of_use_log write failed (continuing): %s", exc)


def write_audit_best_effort(
    *, user_id: str, tenant_id: str, resource_type: str, resource_id: str, purpose: str
) -> None:
    """Best-effort hash-chained audit write for a PHI access. Never blocks."""
    try:
        with get_connection() as conn, conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO platform.audit_log (
                    user_id, tenant_id, action, resource_type, resource_id,
                    purpose, legal_basis, success
                ) VALUES (
                    %(user_id)s, %(tenant_id)s, 'ai.triage.run', %(rtype)s, %(rid)s,
                    %(purpose)s, 'consent', true
                )
                """,
                {
                    "user_id": user_id,
                    "tenant_id": tenant_id,
                    "rtype": resource_type,
                    "rid": resource_id,
                    "purpose": purpose,
                },
            )
    except Exception as exc:  # noqa: BLE001
        logger.warning("audit write failed (continuing): %s", exc)
