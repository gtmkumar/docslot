"""Agentic triage-routing workflow (LangGraph-style, offline-deterministic).

A 4-node pipeline executed as an explicit graph:

    extract_symptoms -> route_department -> score_urgency -> suggest_doctor

Every node is DETERMINISTIC, rule-based reasoning that needs NO external LLM.
The symptom-extraction node has an OPTIONAL pluggable LLM path used ONLY when
`AI_LLM_API_KEY` (or AI_OPENAI_API_KEY) is configured; the offline keyword/
synonym lexicon is the DEFAULT and is always correct on its own. The LLM is
never required and its output is reconciled back to the curated lexicon.

The orchestrator (`run_triage`) persists the workflow, one run, and one step
per node to the ai.* agentic tables — so the schema is genuinely exercised.
"""
from __future__ import annotations

import logging
import time

from . import triage_repository as repo
from .config import get_settings

logger = logging.getLogger("ai_service.triage")


# ---------------------------------------------------------------------------
# Symptom lexicon — canonical symptom -> synonym/keyword phrases
# ---------------------------------------------------------------------------
SYMPTOM_LEXICON: dict[str, list[str]] = {
    "chest pain": ["chest pain", "chest tightness", "crushing chest", "chest pressure",
                   "angina", "pain in chest", "chest discomfort"],
    "breathlessness": ["breathless", "shortness of breath", "short of breath", "sob",
                       "difficulty breathing", "cant breathe", "can't breathe",
                       "gasping", "dyspnea"],
    "palpitations": ["palpitation", "racing heart", "heart racing", "fluttering heart",
                     "irregular heartbeat", "skipped beat"],
    "fever": ["fever", "high temperature", "febrile", "pyrexia", "running a temperature"],
    "cough": ["cough", "coughing", "productive cough", "dry cough"],
    "rash": ["rash", "skin rash", "red skin", "hives", "eruption", "red spots"],
    "itching": ["itch", "itchy", "itching", "pruritus", "scratching"],
    "joint pain": ["joint pain", "knee pain", "arthralgia", "aching joints", "swollen joint"],
    "fracture": ["fracture", "broken bone", "broke my", "bone break"],
    "back pain": ["back pain", "backache", "spine pain", "lower back pain"],
    "abdominal pain": ["abdominal pain", "stomach ache", "stomach pain", "tummy pain",
                       "belly pain", "abdomen pain", "cramps"],
    "headache": ["headache", "migraine", "head pain"],
    "sore throat": ["sore throat", "throat pain", "painful swallowing", "throat infection"],
    "ear pain": ["ear pain", "earache", "ear infection", "hearing loss", "blocked ear"],
    "nosebleed": ["nosebleed", "bleeding nose", "epistaxis", "blocked nose", "sinus",
                  "runny nose"],
    "pregnancy": ["pregnant", "pregnancy", "antenatal", "missed period", "labour", "labor",
                  "morning sickness", "trimester"],
    "vaginal bleeding": ["vaginal bleeding", "menstrual", "period pain", "menstruation",
                         "heavy bleeding", "gynae", "gynaecological"],
    "bleeding": ["severe bleeding", "heavy bleeding", "haemorrhage", "hemorrhage",
                 "bleeding heavily", "blood loss"],
    "stroke signs": ["face drooping", "slurred speech", "weakness on one side",
                     "cant move arm", "numbness on one side", "facial droop"],
    "fainting": ["fainting", "fainted", "passed out", "loss of consciousness", "syncope",
                 "blacked out"],
    "vomiting": ["vomiting", "throwing up", "nausea", "vomit"],
    "diarrhea": ["diarrhea", "diarrhoea", "loose motion", "loose stools"],
    "dizziness": ["dizzy", "dizziness", "lightheaded", "vertigo"],
}


# ---------------------------------------------------------------------------
# Symptom -> specialty (department name) routing map.
# Order matters: earlier entries win when multiple symptoms are present.
# ---------------------------------------------------------------------------
SYMPTOM_TO_DEPARTMENT: list[tuple[str, str]] = [
    ("chest pain", "Cardiology"),
    ("palpitations", "Cardiology"),
    ("stroke signs", "General Medicine"),
    ("pregnancy", "Gynaecology"),
    ("vaginal bleeding", "Gynaecology"),
    ("fracture", "Orthopedics"),
    ("joint pain", "Orthopedics"),
    ("back pain", "Orthopedics"),
    ("rash", "Dermatology"),
    ("itching", "Dermatology"),
    ("ear pain", "ENT"),
    ("sore throat", "ENT"),
    ("nosebleed", "ENT"),
    ("abdominal pain", "General Medicine"),
    ("headache", "General Medicine"),
    ("breathlessness", "General Medicine"),
    ("cough", "General Medicine"),
    ("fever", "General Medicine"),
]

# Symptoms that, in a paediatric context (young child), pull to Paediatrics.
PAEDIATRIC_PULL = {"fever", "cough", "sore throat", "ear pain", "rash", "vomiting",
                   "diarrhea", "abdominal pain"}
PAEDIATRIC_AGE_MAX = 12

DEFAULT_DEPARTMENT = "General Medicine"


# ---------------------------------------------------------------------------
# Red-flag rules for urgency scoring
# ---------------------------------------------------------------------------
# (band, human-readable flag, predicate over the symptom set)
def _has(symptoms: set[str], *names: str) -> bool:
    return all(n in symptoms for n in names)


RED_FLAG_RULES: list[tuple[str, str, object]] = [
    ("emergency", "chest pain with breathlessness (possible cardiac event)",
     lambda s: _has(s, "chest pain", "breathlessness")),
    ("emergency", "stroke warning signs",
     lambda s: "stroke signs" in s),
    ("emergency", "severe / heavy bleeding",
     lambda s: "bleeding" in s),
    ("emergency", "loss of consciousness / fainting",
     lambda s: "fainting" in s),
    ("high", "isolated chest pain",
     lambda s: "chest pain" in s),
    ("high", "acute breathlessness",
     lambda s: "breathlessness" in s),
    ("high", "palpitations",
     lambda s: "palpitations" in s),
    ("medium", "fever",
     lambda s: "fever" in s),
    ("medium", "abdominal pain",
     lambda s: "abdominal pain" in s),
    ("medium", "fracture (suspected)",
     lambda s: "fracture" in s),
    ("medium", "persistent vomiting / diarrhea",
     lambda s: "vomiting" in s or "diarrhea" in s),
]

_BAND_RANK = {"low": 0, "medium": 1, "high": 2, "emergency": 3}


# ===========================================================================
# NODE 1 — extract_symptoms
# ===========================================================================
def _extract_offline(complaint: str) -> list[str]:
    """Keyword/synonym match against the curated lexicon (order-stable)."""
    text = complaint.lower()
    found: list[str] = []
    for symptom, phrases in SYMPTOM_LEXICON.items():
        if any(phrase in text for phrase in phrases):
            found.append(symptom)
    return found


def _extract_llm(complaint: str) -> list[str] | None:
    """OPTIONAL LLM symptom extraction. Returns None unless a key is configured
    AND the call succeeds. Output is reconciled to the curated lexicon so the
    rest of the deterministic pipeline stays well-defined.
    """
    settings = get_settings()
    api_key = settings.llm_api_key or settings.openai_api_key
    if not api_key:
        return None

    lexicon = ", ".join(SYMPTOM_LEXICON.keys())
    system = (
        "You are a clinical triage assistant. Extract the patient's symptoms "
        "from their complaint and map each to EXACTLY one label from this list: "
        f"{lexicon}. Reply with ONLY a comma-separated list of the matching "
        "labels, nothing else. If none match, reply NONE."
    )
    try:
        import httpx

        base = settings.llm_base_url.rstrip("/")
        resp = httpx.post(
            f"{base}/v1/messages",
            headers={
                "x-api-key": api_key,
                "anthropic-version": "2023-06-01",
                "content-type": "application/json",
            },
            json={
                "model": settings.llm_model,
                "max_tokens": 120,
                "system": system,
                "messages": [{"role": "user", "content": complaint}],
            },
            timeout=20.0,
        )
        resp.raise_for_status()
        blocks = resp.json().get("content", [])
        text = "".join(b.get("text", "") for b in blocks if b.get("type") == "text")
        # Reconcile to lexicon keys (keep curated order).
        raw = {p.strip().lower() for p in text.replace("\n", ",").split(",")}
        return [k for k in SYMPTOM_LEXICON if k in raw]
    except Exception as exc:  # noqa: BLE001 — fall back to offline
        logger.warning("LLM symptom extraction failed; using offline path: %s", exc)
        return None


def node_extract_symptoms(state: dict) -> dict:
    """NODE 1: free-text complaint -> normalized symptom list.

    Offline keyword path by default; optional LLM path only when configured.
    Always backstops with the offline result so a symptom is never lost.
    """
    complaint = state["complaint"]
    offline = _extract_offline(complaint)

    path = "rule"
    symptoms = offline
    llm = _extract_llm(complaint)
    if llm is not None:
        path = "llm"
        # Union so the LLM can add nuance but never drop a clear keyword hit.
        merged = list(dict.fromkeys(offline + llm))
        symptoms = [k for k in SYMPTOM_LEXICON if k in set(merged)]

    return {"symptoms": symptoms, "extraction_path": path}


# ===========================================================================
# NODE 2 — route_department
# ===========================================================================
def node_route_department(state: dict, available_departments: list[str]) -> dict:
    """NODE 2: symptoms -> one of the tenant's real departments.

    Routes to the first mapped department that the tenant actually has. Applies
    the paediatric pull for young children. Defaults to General Medicine.
    """
    symptoms = set(state["symptoms"])
    age = state.get("patient_age")
    dept_set = set(available_departments)

    # Paediatric context: a young child with a paediatric-pull symptom and no
    # hard specialty red flag goes to Paediatrics if the tenant has it.
    hard_specialty = symptoms & {"chest pain", "palpitations", "fracture",
                                 "pregnancy", "vaginal bleeding", "stroke signs"}
    if (
        age is not None
        and age <= PAEDIATRIC_AGE_MAX
        and (symptoms & PAEDIATRIC_PULL)
        and not hard_specialty
        and "Paediatrics" in dept_set
    ):
        return {"department": "Paediatrics", "routing_reason": "paediatric_context"}

    matched: list[str] = []
    chosen = DEFAULT_DEPARTMENT
    for symptom, dept in SYMPTOM_TO_DEPARTMENT:
        if symptom in symptoms and dept in dept_set:
            chosen = dept
            matched.append(symptom)
            break

    if chosen not in dept_set:
        chosen = DEFAULT_DEPARTMENT if DEFAULT_DEPARTMENT in dept_set else (
            available_departments[0] if available_departments else DEFAULT_DEPARTMENT
        )

    return {
        "department": chosen,
        "routing_reason": "symptom_map" if matched else "default",
        "routing_symptoms": matched,
    }


# ===========================================================================
# NODE 3 — score_urgency
# ===========================================================================
def node_score_urgency(state: dict) -> dict:
    """NODE 3: red-flag rules -> band (low|medium|high|emergency) + matched flags."""
    symptoms = set(state["symptoms"])
    band = "low"
    red_flags: list[str] = []
    for rule_band, flag, predicate in RED_FLAG_RULES:
        if predicate(symptoms):
            red_flags.append(flag)
            if _BAND_RANK[rule_band] > _BAND_RANK[band]:
                band = rule_band

    # If we recognized symptoms but nothing tripped a rule, it's a mild low.
    if not symptoms:
        band = "low"
    return {"urgency": {"band": band, "redFlags": red_flags}}


# ===========================================================================
# NODE 4 — suggest_doctor
# ===========================================================================
def node_suggest_doctor(state: dict, tenant_id: str) -> dict:
    """NODE 4: routed department's active doctors + each one's next free slot."""
    doctors = repo.suggest_doctors(tenant_id, state["department"], limit=3)
    return {"suggestedDoctors": doctors}


# ===========================================================================
# Graph definition (persisted to ai.ai_workflows.graph_definition)
# ===========================================================================
GRAPH_DEFINITION = {
    "engine": "docslot-mini-langgraph",
    "entry": "extract_symptoms",
    "nodes": [
        {"name": "extract_symptoms", "type": "llm_or_rule",
         "description": "Free-text complaint -> normalized symptoms (offline "
                        "lexicon default; optional LLM path)."},
        {"name": "route_department", "type": "rule",
         "description": "Symptoms -> one of the tenant's departments."},
        {"name": "score_urgency", "type": "rule",
         "description": "Red-flag rules -> low|medium|high|emergency."},
        {"name": "suggest_doctor", "type": "tool",
         "description": "Routed department's active doctors + next available slot."},
    ],
    "edges": [
        {"from": "extract_symptoms", "to": "route_department"},
        {"from": "route_department", "to": "score_urgency"},
        {"from": "score_urgency", "to": "suggest_doctor"},
        {"from": "suggest_doctor", "to": "END"},
    ],
}


# ===========================================================================
# Orchestrator — run the graph and persist run + steps
# ===========================================================================
def run_triage(
    *,
    tenant_id: str,
    user_id: str,
    complaint: str,
    patient_age: int | None,
    patient_id: str | None,
    booking_id: str | None,
) -> dict:
    """Execute the 4-node pipeline, persisting the run and one step per node.

    Returns the assembled triage result (also stored as the run's output_data).
    Tenant isolation: workflow is global; the run + every lookup are tenant-scoped.
    """
    workflow_id = repo.upsert_workflow(GRAPH_DEFINITION)

    input_data = {
        "complaint": complaint,
        "patientAge": patient_age,
        "patientId": patient_id,
        "bookingId": booking_id,
    }
    related_type = "patient" if patient_id else ("booking" if booking_id else None)
    related_id = patient_id or booking_id

    run_id = repo.create_run(
        workflow_id=workflow_id,
        tenant_id=tenant_id,
        user_id=user_id,
        input_data=input_data,
        related_resource_type=related_type,
        related_resource_id=related_id,
    )

    run_t0 = time.perf_counter()
    departments = repo.list_departments(tenant_id)
    state: dict = {"complaint": complaint, "patient_age": patient_age}
    step_summaries: list[dict] = []

    # Plan: (node_name, db_step_type, callable, summary_fn)
    def _timed(fn):
        t0 = time.perf_counter()
        delta = fn()
        return delta, int((time.perf_counter() - t0) * 1000)

    try:
        # --- Step 1: extract_symptoms ---
        delta, ms = _timed(lambda: node_extract_symptoms(state))
        state.update(delta)
        db_type = "llm_call" if state.get("extraction_path") == "llm" else "tool_call"
        model_used = (
            get_settings().llm_model if state.get("extraction_path") == "llm" else None
        )
        repo.insert_step(
            run_id=run_id, step_number=1, node_name="extract_symptoms",
            step_type=db_type, tool_name="symptom_lexicon",
            tool_input={"complaint": complaint},
            tool_output={"symptoms": state["symptoms"],
                         "path": state["extraction_path"]},
            duration_ms=ms, model_used=model_used,
        )
        step_summaries.append({
            "stepNumber": 1, "nodeName": "extract_symptoms", "stepType": db_type,
            "summary": f"Extracted {len(state['symptoms'])} symptom(s) "
                       f"[{state['extraction_path']} path]: "
                       f"{', '.join(state['symptoms']) or 'none'}",
        })

        # --- Step 2: route_department ---
        delta, ms = _timed(lambda: node_route_department(state, departments))
        state.update(delta)
        repo.insert_step(
            run_id=run_id, step_number=2, node_name="route_department",
            step_type="condition", tool_name="symptom_department_map",
            tool_input={"symptoms": state["symptoms"],
                        "availableDepartments": departments,
                        "patientAge": patient_age},
            tool_output={"department": state["department"],
                         "reason": state["routing_reason"]},
            duration_ms=ms,
        )
        step_summaries.append({
            "stepNumber": 2, "nodeName": "route_department", "stepType": "condition",
            "summary": f"Routed to {state['department']} "
                       f"({state['routing_reason']})",
        })

        # --- Step 3: score_urgency ---
        delta, ms = _timed(lambda: node_score_urgency(state))
        state.update(delta)
        repo.insert_step(
            run_id=run_id, step_number=3, node_name="score_urgency",
            step_type="condition", tool_name="red_flag_rules",
            tool_input={"symptoms": state["symptoms"]},
            tool_output=state["urgency"],
            duration_ms=ms,
        )
        rf = state["urgency"]["redFlags"]
        step_summaries.append({
            "stepNumber": 3, "nodeName": "score_urgency", "stepType": "condition",
            "summary": f"Urgency {state['urgency']['band']}"
                       + (f"; flags: {', '.join(rf)}" if rf else ""),
        })

        # --- Step 4: suggest_doctor ---
        delta, ms = _timed(lambda: node_suggest_doctor(state, tenant_id))
        state.update(delta)
        repo.insert_step(
            run_id=run_id, step_number=4, node_name="suggest_doctor",
            step_type="tool_call", tool_name="doctor_slot_lookup",
            tool_input={"department": state["department"], "tenantScoped": True},
            tool_output={"suggestedDoctors": state["suggestedDoctors"]},
            duration_ms=ms,
        )
        step_summaries.append({
            "stepNumber": 4, "nodeName": "suggest_doctor", "stepType": "tool_call",
            "summary": f"Suggested {len(state['suggestedDoctors'])} doctor(s) in "
                       f"{state['department']}",
        })

    except Exception as exc:  # noqa: BLE001
        repo.finalize_run_failed(
            run_id=run_id, tenant_id=tenant_id,
            node=state.get("_node", "unknown"), message=str(exc),
        )
        raise

    output_data = {
        "symptoms": state["symptoms"],
        "department": state["department"],
        "urgency": state["urgency"],
        "suggestedDoctors": state["suggestedDoctors"],
        "extractionPath": state["extraction_path"],
    }

    total_ms = int((time.perf_counter() - run_t0) * 1000)
    repo.finalize_run_success(
        run_id=run_id, tenant_id=tenant_id, output_data=output_data,
        duration_ms=total_ms, iterations_used=4,
    )

    return {
        "runId": run_id,
        "workflowKey": repo.WORKFLOW_KEY,
        "symptoms": state["symptoms"],
        "department": state["department"],
        "urgency": state["urgency"],
        "suggestedDoctors": state["suggestedDoctors"],
        "steps": step_summaries,
    }
