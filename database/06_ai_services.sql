-- ============================================================================
-- DocSlot Platform Database — Part 6: AI Services Schema
-- ============================================================================
-- This adds tables for AI-powered features. Owned by the Python AI service
-- (DocSlot.AI) but accessible to .NET via shared schema.
--
-- WHAT GOES IN THE PYTHON SERVICE (not in this SQL file):
-- - LangGraph workflows (multi-agent triage, prescription review, RAG)
-- - LangChain RAG pipelines (medical history Q&A)
-- - pandas / scikit-learn for analytics (no-show prediction, demand forecasting)
-- - Embeddings generation (Claude, OpenAI, Gemini, local models)
-- - OCR pipelines (PaddleOCR, Tesseract for lab reports, prescriptions)
-- - Speech-to-text (Whisper for voice notes in WhatsApp)
--
-- WHAT GOES IN THIS SQL FILE:
-- - Tables that PERSIST AI state and outputs (workflows, agent runs, embeddings)
-- - Tables that RBAC-control AI access (AI permissions, model configs)
-- - Tables that AUDIT AI actions (every AI inference logged)
-- - Tables that link AI outputs back to DocSlot domain (e.g., AI-generated
--   prescription suggestions linked to docslot.prescriptions)
--
-- DESIGN PRINCIPLES:
-- 1. AI is a service, not a feature — has its own schema (ai.*)
-- 2. Every AI inference is audited like any other data access
-- 3. AI outputs are advisory by default — humans approve before action
-- 4. Patient data sent to AI requires explicit consent (DPDP Section 6)
-- 5. AI models are configurable per-tenant (some prefer Claude, some prefer
--    Azure OpenAI, some prefer on-prem models for data sovereignty)
-- 6. Embeddings stored encrypted (they can be reverse-engineered to leak PII)
--
-- EXECUTION:
--   psql -d docslot_platform -f 06_ai_services.sql
--
-- Runs after 05_security_hardening.sql.
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS ai;
COMMENT ON SCHEMA ai IS 'AI/ML services schema — owned by DocSlot.AI Python service. RBAC and audit through platform.*';

-- Enable pgvector extension for embeddings storage and similarity search
-- (Available in PostgreSQL 16+ with the pgvector extension installed)
-- If pgvector is not available, fall back to BYTEA storage and external vector DB.
DO $$
BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS vector;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'pgvector extension not available — falling back to BYTEA storage. Install pgvector for native vector ops.';
    END;
END $$;

-- ============================================================================
-- TABLE AI1: AI_MODEL_CONFIGS (which AI providers each tenant uses)
-- ============================================================================
-- Tenants may have data sovereignty requirements: some hospitals require
-- on-premise models, some allow Claude/OpenAI cloud, some specifically
-- want Azure OpenAI for HIPAA-compliant zones. This table is the central
-- registry of model configurations.
CREATE TABLE ai.ai_model_configs (
    config_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform default config
    use_case            VARCHAR(50) NOT NULL,                     -- 'triage', 'rag_medical', 'prescription_review', 'no_show_prediction', 'ocr_lab_report'
    provider            VARCHAR(30) NOT NULL CHECK (provider IN ('anthropic', 'openai', 'azure_openai', 'google_vertex', 'aws_bedrock', 'local_ollama', 'on_prem_llama', 'cohere')),
    model_name          VARCHAR(100) NOT NULL,                    -- 'claude-opus-4-7', 'gpt-4o', 'gemini-2-pro', 'llama-3-70b'
    endpoint_url        VARCHAR(500),                              -- For self-hosted models
    credential_reference VARCHAR(500),                              -- KMS reference, not the key itself
    max_tokens          INT DEFAULT 4000,
    temperature         DECIMAL(3,2) DEFAULT 0.0,
    system_prompt_template TEXT,
    cost_per_1k_input_tokens DECIMAL(10,6),                       -- For cost tracking
    cost_per_1k_output_tokens DECIMAL(10,6),

    -- Compliance flags
    data_residency_region VARCHAR(10),                            -- 'IN', 'US', 'EU' — where inference runs
    bao_signed          BOOLEAN NOT NULL DEFAULT false,           -- Business Associate Agreement signed?
    allows_phi          BOOLEAN NOT NULL DEFAULT false,           -- Can this model receive PHI?
    requires_consent    BOOLEAN NOT NULL DEFAULT true,            -- Patient consent required for use

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_default_for_use_case BOOLEAN NOT NULL DEFAULT false,

    -- Audit
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata            JSONB NOT NULL DEFAULT '{}',

    UNIQUE(tenant_id, use_case, provider, model_name)
);

CREATE INDEX idx_ai_configs_use_case ON ai.ai_model_configs(use_case, tenant_id) WHERE is_active = true;
CREATE INDEX idx_ai_configs_default ON ai.ai_model_configs(use_case) WHERE is_default_for_use_case = true AND is_active = true;

CREATE TRIGGER trg_ai_configs_updated_at BEFORE UPDATE ON ai.ai_model_configs
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI2: AI_WORKFLOWS (LangGraph workflow definitions)
-- ============================================================================
-- A LangGraph workflow is a stateful directed graph of LLM/tool calls.
-- This table stores workflow definitions versioned and tenant-scoped.
-- Examples:
-- - Triage workflow: WhatsApp message → intent classify → severity assess → route
-- - Prescription review: prescription → drug interaction check → allergy check → safety report
-- - RAG over medical history: question → retrieve consents → fetch records → answer
CREATE TABLE ai.ai_workflows (
    workflow_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_key        VARCHAR(100) NOT NULL,                    -- 'symptom_triage_v2', 'prescription_safety_check'
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide workflow
    name                VARCHAR(200) NOT NULL,
    description         TEXT,
    version             INT NOT NULL DEFAULT 1,
    use_case            VARCHAR(50) NOT NULL,                     -- Maps to ai_model_configs.use_case

    -- LangGraph workflow definition (serialized)
    graph_definition    JSONB NOT NULL,                           -- Nodes, edges, conditions
    initial_state_schema JSONB,                                    -- Pydantic schema for initial state
    output_schema       JSONB,                                    -- Expected output structure

    -- Execution config
    timeout_seconds     INT NOT NULL DEFAULT 30,
    max_iterations      INT NOT NULL DEFAULT 10,                  -- For agent loops
    requires_human_approval BOOLEAN NOT NULL DEFAULT false,       -- Output needs human review before action

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,           -- Workflows start inactive until tested
    activated_at        TIMESTAMPTZ,
    activated_by_user_id UUID REFERENCES platform.users(user_id),
    deprecated_at       TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(workflow_key, version, tenant_id)
);

CREATE INDEX idx_workflows_active ON ai.ai_workflows(workflow_key, tenant_id) WHERE is_active = true;
CREATE INDEX idx_workflows_use_case ON ai.ai_workflows(use_case) WHERE is_active = true;

CREATE TRIGGER trg_workflows_updated_at BEFORE UPDATE ON ai.ai_workflows
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI3: AI_AGENT_RUNS (every workflow execution logged)
-- ============================================================================
-- Each time a LangGraph workflow executes, we log the run for audit, debugging,
-- and cost tracking. Includes full input/output for replay and offline eval.
CREATE TABLE ai.ai_agent_runs (
    run_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_id         UUID NOT NULL REFERENCES ai.ai_workflows(workflow_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Context (who/what triggered this)
    triggered_by_user_id UUID REFERENCES platform.users(user_id),
    triggered_by_event  VARCHAR(100),                              -- 'whatsapp.message.received', 'booking.created'
    related_resource_type VARCHAR(50),                              -- 'booking', 'patient', 'prescription'
    related_resource_id UUID,
    correlation_id      VARCHAR(100),                              -- For tracing across services

    -- Input
    input_data          JSONB NOT NULL,                            -- Initial state passed to workflow
    input_token_count   INT,

    -- Execution
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'running', 'awaiting_human', 'success', 'failed', 'timeout', 'cancelled')),
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    duration_ms         INT,
    iterations_used     INT,

    -- Output
    output_data         JSONB,
    output_token_count  INT,
    confidence_score    DECIMAL(4,3),                              -- 0.000 to 1.000

    -- Cost tracking
    total_cost_usd      DECIMAL(10,6),
    models_used         JSONB,                                     -- [{model, input_tokens, output_tokens, cost}]

    -- Human approval (when required)
    human_approval_required BOOLEAN NOT NULL DEFAULT false,
    approved_at         TIMESTAMPTZ,
    approved_by_user_id UUID REFERENCES platform.users(user_id),
    approval_notes      TEXT,
    rejected_at         TIMESTAMPTZ,
    rejected_by_user_id UUID REFERENCES platform.users(user_id),
    rejection_reason    TEXT,

    -- Errors
    error_code          VARCHAR(50),
    error_message       TEXT,
    failed_at_node      VARCHAR(100),                              -- Which workflow node failed

    -- Compliance (DPDP)
    patient_consent_id  UUID REFERENCES docslot.abdm_consents(consent_id),  -- If PHI was used
    data_classes_accessed VARCHAR(50)[]                            -- What data classes the AI saw
);

CREATE INDEX idx_runs_workflow ON ai.ai_agent_runs(workflow_id, started_at DESC);
CREATE INDEX idx_runs_tenant ON ai.ai_agent_runs(tenant_id, started_at DESC);
CREATE INDEX idx_runs_pending_approval ON ai.ai_agent_runs(tenant_id, started_at) WHERE status = 'awaiting_human';
CREATE INDEX idx_runs_failed ON ai.ai_agent_runs(workflow_id, started_at DESC) WHERE status = 'failed';
CREATE INDEX idx_runs_resource ON ai.ai_agent_runs(related_resource_type, related_resource_id);
CREATE INDEX idx_runs_correlation ON ai.ai_agent_runs(correlation_id) WHERE correlation_id IS NOT NULL;

COMMENT ON TABLE ai.ai_agent_runs IS 'Every LangGraph workflow run logged. Partition by month at scale.';

-- ============================================================================
-- TABLE AI4: AI_AGENT_STEPS (individual node executions within a workflow run)
-- ============================================================================
-- For debugging and observability — each node in a LangGraph workflow logs here.
CREATE TABLE ai.ai_agent_steps (
    step_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id              UUID NOT NULL REFERENCES ai.ai_agent_runs(run_id) ON DELETE CASCADE,
    step_number         INT NOT NULL,
    node_name           VARCHAR(100) NOT NULL,                    -- LangGraph node identifier
    step_type           VARCHAR(30) NOT NULL CHECK (step_type IN ('llm_call', 'tool_call', 'condition', 'human_input', 'parallel', 'aggregator')),

    -- LLM call details (if step_type = 'llm_call')
    model_used          VARCHAR(100),
    prompt              TEXT,                                      -- Full prompt sent
    response            TEXT,                                      -- Full response received
    input_tokens        INT,
    output_tokens       INT,
    cost_usd            DECIMAL(10,6),

    -- Tool call details (if step_type = 'tool_call')
    tool_name           VARCHAR(100),
    tool_input          JSONB,
    tool_output         JSONB,

    -- Timing
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    duration_ms         INT,

    -- Outcome
    success             BOOLEAN NOT NULL DEFAULT true,
    error_message       TEXT,

    -- State snapshot after step
    state_snapshot      JSONB,                                     -- LangGraph state after this node
    UNIQUE(run_id, step_number)
);

CREATE INDEX idx_steps_run ON ai.ai_agent_steps(run_id, step_number);
CREATE INDEX idx_steps_failed ON ai.ai_agent_steps(run_id) WHERE success = false;
CREATE INDEX idx_steps_expensive ON ai.ai_agent_steps(cost_usd DESC, started_at DESC) WHERE cost_usd IS NOT NULL;

-- ============================================================================
-- TABLE AI5: AI_PROMPTS (versioned prompt library)
-- ============================================================================
-- Centralized prompt management. Treats prompts as code: versioned, tested,
-- with rollback capability. Critical for medical applications where prompt
-- changes can affect patient safety.
CREATE TABLE ai.ai_prompts (
    prompt_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prompt_key          VARCHAR(100) NOT NULL,                    -- 'symptom_triage_system', 'prescription_safety_check'
    version             INT NOT NULL DEFAULT 1,
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide prompt

    -- Content
    system_prompt       TEXT NOT NULL,
    user_prompt_template TEXT,                                     -- With {variables}
    expected_variables  VARCHAR(100)[],

    -- Metadata
    use_case            VARCHAR(50) NOT NULL,
    description         TEXT,
    tested_on_models    VARCHAR(100)[],                            -- Models this was tested with

    -- Safety
    pii_handling_notes  TEXT,                                       -- How PII is masked in this prompt
    medical_safety_review_required BOOLEAN NOT NULL DEFAULT false,
    medical_review_by_user_id UUID REFERENCES platform.users(user_id),
    medical_review_at   TIMESTAMPTZ,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT false,
    activated_at        TIMESTAMPTZ,
    deprecated_at       TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    UNIQUE(prompt_key, version, tenant_id)
);

CREATE INDEX idx_prompts_active ON ai.ai_prompts(prompt_key, tenant_id) WHERE is_active = true;

-- ============================================================================
-- TABLE AI6: EMBEDDINGS (vector store for RAG over medical history)
-- ============================================================================
-- Stores document embeddings for retrieval-augmented generation.
-- Embeddings encrypted at rest because they can leak PII through inversion attacks.
--
-- pgvector: if available, uses vector type natively. Otherwise BYTEA fallback.
-- For large deployments, consider external vector DB (Pinecone, Weaviate, Qdrant)
-- and use this table only as a registry/metadata layer.
CREATE TABLE ai.embeddings (
    embedding_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Source document
    source_type         VARCHAR(50) NOT NULL,                     -- 'prescription', 'lab_report', 'medical_history_note', 'patient_query'
    source_id           UUID NOT NULL,                             -- Reference to the source row
    chunk_index         INT NOT NULL DEFAULT 0,                   -- For chunked documents
    chunk_text          TEXT,                                      -- The text that was embedded (encrypted)
    chunk_text_hash     VARCHAR(64),                               -- For deduplication

    -- Embedding
    embedding_model     VARCHAR(100) NOT NULL,                     -- 'voyage-2', 'text-embedding-3-large', 'cohere-embed-v3'
    embedding_dimensions INT NOT NULL,                             -- 1024, 1536, 3072
    embedding_vector    BYTEA,                                     -- Encrypted vector (use vector type if pgvector available)

    -- Metadata for filtering during retrieval
    metadata            JSONB NOT NULL DEFAULT '{}',              -- {patient_id, date, doctor_id, category}
    patient_id          UUID REFERENCES docslot.patients(patient_id),
                                                                   -- Denormalized for fast filtering

    -- Encryption
    encryption_key_id   UUID REFERENCES platform.encryption_keys(key_id),

    -- Lifecycle
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ,                               -- For TTL on patient-query embeddings
    deleted_at          TIMESTAMPTZ                                -- For DPDP erasure
);

CREATE INDEX idx_embeddings_source ON ai.embeddings(source_type, source_id);
CREATE INDEX idx_embeddings_patient ON ai.embeddings(patient_id) WHERE patient_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX idx_embeddings_tenant ON ai.embeddings(tenant_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_embeddings_dedup ON ai.embeddings(chunk_text_hash);
CREATE INDEX idx_embeddings_expires ON ai.embeddings(expires_at) WHERE expires_at IS NOT NULL AND deleted_at IS NULL;

-- If pgvector available, add vector similarity index
-- CREATE INDEX idx_embeddings_vector ON ai.embeddings USING ivfflat (embedding_vector vector_cosine_ops);

COMMENT ON TABLE ai.embeddings IS 'Vector embeddings for RAG. Encrypted at rest. Consider external vector DB for >10M vectors.';

-- ============================================================================
-- TABLE AI7: AI_KNOWLEDGE_BASES (registry of RAG knowledge sources)
-- ============================================================================
-- A knowledge base is a collection of documents indexed for retrieval.
-- Examples: medical drug interaction database, hospital protocols, FAQ.
CREATE TABLE ai.ai_knowledge_bases (
    kb_id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    kb_key              VARCHAR(100) NOT NULL,                    -- 'drug_interactions', 'hospital_protocols', 'patient_history'
    tenant_id           UUID REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
                                                                  -- NULL = platform-wide KB
    name                VARCHAR(200) NOT NULL,
    description         TEXT,
    source_type         VARCHAR(50) NOT NULL,                     -- 'medical_database', 'tenant_documents', 'patient_records'
    embedding_model     VARCHAR(100) NOT NULL,
    document_count      INT NOT NULL DEFAULT 0,
    last_indexed_at     TIMESTAMPTZ,

    -- Access control
    requires_consent    BOOLEAN NOT NULL DEFAULT false,           -- e.g., patient KB needs patient consent
    permission_required VARCHAR(150),                              -- RBAC permission to query this KB

    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(kb_key, tenant_id)
);

-- ============================================================================
-- TABLE AI8: AI_FEEDBACK (user feedback on AI outputs for improvement)
-- ============================================================================
-- Critical for medical AI: doctors need to flag wrong AI outputs.
-- This feeds back into model evaluation and prompt tuning.
CREATE TABLE ai.ai_feedback (
    feedback_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id              UUID NOT NULL REFERENCES ai.ai_agent_runs(run_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),
    feedback_type       VARCHAR(30) NOT NULL CHECK (feedback_type IN ('correct', 'incorrect', 'partial', 'irrelevant', 'unsafe', 'biased')),
    severity            VARCHAR(20) CHECK (severity IS NULL OR severity IN ('low', 'medium', 'high', 'critical')),
    notes               TEXT,
    suggested_correct_output JSONB,                                -- What it should have said
    requires_immediate_action BOOLEAN NOT NULL DEFAULT false,
    triggered_workflow_rollback BOOLEAN NOT NULL DEFAULT false,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_feedback_run ON ai.ai_feedback(run_id);
CREATE INDEX idx_feedback_critical ON ai.ai_feedback(severity, created_at DESC) WHERE severity IN ('high', 'critical');
CREATE INDEX idx_feedback_unsafe ON ai.ai_feedback(created_at DESC) WHERE feedback_type IN ('unsafe', 'biased');

-- ============================================================================
-- TABLE AI9: AI_DOCUMENT_EXTRACTIONS (OCR + structured extraction results)
-- ============================================================================
-- For lab reports, prescriptions (paper), insurance cards.
-- Python service runs OCR + LLM extraction; results stored here for review.
CREATE TABLE ai.ai_document_extractions (
    extraction_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    -- Source document
    source_type         VARCHAR(50) NOT NULL,                     -- 'lab_report_pdf', 'paper_prescription', 'insurance_card'
    source_url          TEXT NOT NULL,
    source_mime_type    VARCHAR(100),
    source_size_bytes   BIGINT,

    -- Related domain entity (if known)
    related_booking_id  UUID REFERENCES docslot.bookings(booking_id),
    related_patient_id  UUID REFERENCES docslot.patients(patient_id),

    -- Extraction pipeline
    ocr_engine          VARCHAR(50),                               -- 'paddleocr', 'tesseract', 'google_vision', 'azure_document_intelligence'
    raw_ocr_text        TEXT,                                       -- Encrypted
    extraction_model    VARCHAR(100),                              -- LLM used for structured extraction
    extracted_data      JSONB,                                     -- Structured output

    -- Confidence and review
    overall_confidence  DECIMAL(4,3),                              -- 0.000 to 1.000
    requires_human_review BOOLEAN NOT NULL DEFAULT true,
    reviewed_by_user_id UUID REFERENCES platform.users(user_id),
    reviewed_at         TIMESTAMPTZ,
    review_status       VARCHAR(20) CHECK (review_status IS NULL OR review_status IN ('approved', 'rejected', 'corrected')),
    corrected_data      JSONB,                                     -- Human-corrected output (training data)

    -- Status
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'extracted', 'reviewed', 'approved', 'failed')),

    -- Encryption
    encryption_key_id   UUID REFERENCES platform.encryption_keys(key_id),

    -- Cost
    cost_usd            DECIMAL(10,6),

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_extractions_pending_review ON ai.ai_document_extractions(tenant_id, created_at)
    WHERE requires_human_review = true AND reviewed_at IS NULL;
CREATE INDEX idx_extractions_patient ON ai.ai_document_extractions(related_patient_id) WHERE related_patient_id IS NOT NULL;
CREATE INDEX idx_extractions_booking ON ai.ai_document_extractions(related_booking_id) WHERE related_booking_id IS NOT NULL;

CREATE TRIGGER trg_extractions_updated_at BEFORE UPDATE ON ai.ai_document_extractions
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE AI10: AI_PREDICTIONS (no-show, demand forecasting, risk scores)
-- ============================================================================
-- For non-LLM ML models: scikit-learn classifiers, XGBoost, etc.
-- These predictions feed dashboards and operational decisions.
CREATE TABLE ai.ai_predictions (
    prediction_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    model_name          VARCHAR(100) NOT NULL,                    -- 'no_show_classifier_v3', 'demand_forecast_lstm_v2'
    model_version       VARCHAR(50) NOT NULL,
    prediction_type     VARCHAR(50) NOT NULL CHECK (prediction_type IN ('no_show_probability', 'demand_forecast', 'patient_risk_score', 'churn_probability', 'fraud_score', 'cancellation_risk')),

    -- Subject
    related_resource_type VARCHAR(50),                              -- 'booking', 'patient', 'doctor'
    related_resource_id UUID,

    -- Prediction
    predicted_value     DECIMAL(10,4) NOT NULL,                    -- Probability or value
    confidence_interval JSONB,                                     -- {lower, upper}
    features_used       JSONB,                                     -- Feature snapshot at prediction time

    -- Outcome (filled in later for model evaluation)
    actual_outcome      DECIMAL(10,4),
    outcome_observed_at TIMESTAMPTZ,

    -- Validity
    valid_until         TIMESTAMPTZ,                               -- Predictions expire (booking-specific predictions)
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_predictions_resource ON ai.ai_predictions(related_resource_type, related_resource_id);
CREATE INDEX idx_predictions_type ON ai.ai_predictions(prediction_type, created_at DESC);
-- NOTE: predicate cannot use NOW() (must be IMMUTABLE). Queries should add
-- "AND valid_until < NOW()" themselves to find truly expired-but-unevaluated rows.
CREATE INDEX idx_predictions_unevaluated ON ai.ai_predictions(prediction_type) WHERE actual_outcome IS NULL;

-- ============================================================================
-- AI-SPECIFIC PERMISSIONS (registered with platform.permissions)
-- ============================================================================
DO $$
DECLARE
    ai_product_id UUID;
BEGIN
    -- Register AI as a "product" (it's a meta-product cutting across all)
    INSERT INTO platform.products (product_key, name, description, schema_name)
    VALUES ('ai', 'AI Services', 'AI/ML capabilities across all products', 'ai')
    ON CONFLICT (product_key) DO NOTHING
    RETURNING product_id INTO ai_product_id;

    IF ai_product_id IS NULL THEN
        SELECT product_id INTO ai_product_id FROM platform.products WHERE product_key = 'ai';
    END IF;

    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    -- Model configuration
    ('ai.models.read', ai_product_id, 'ai_model_configs', 'read', 'tenant', 'View AI model configurations', false),
    ('ai.models.manage', ai_product_id, 'ai_model_configs', 'update', 'tenant', 'Configure AI models for tenant', true),

    -- Workflows
    ('ai.workflows.read', ai_product_id, 'ai_workflows', 'read', 'tenant', 'View AI workflows', false),
    ('ai.workflows.create', ai_product_id, 'ai_workflows', 'create', 'tenant', 'Create custom workflows', true),
    ('ai.workflows.activate', ai_product_id, 'ai_workflows', 'update', 'tenant', 'Activate workflows', true),
    ('ai.workflows.execute', ai_product_id, 'ai_workflows', 'create', 'tenant', 'Trigger workflow execution', false),

    -- Runs and observability
    ('ai.runs.read', ai_product_id, 'ai_agent_runs', 'read', 'tenant', 'View workflow runs', false),
    ('ai.runs.approve', ai_product_id, 'ai_agent_runs', 'update', 'tenant', 'Approve runs requiring human approval', false),

    -- Prompts (sensitive — prompt changes affect patient safety)
    ('ai.prompts.read', ai_product_id, 'ai_prompts', 'read', 'tenant', 'View prompts', false),
    ('ai.prompts.create', ai_product_id, 'ai_prompts', 'create', 'tenant', 'Create new prompts', true),
    ('ai.prompts.medical_review', ai_product_id, 'ai_prompts', 'update', 'platform', 'Medically review prompts (qualified medical reviewer)', true),
    ('ai.prompts.activate', ai_product_id, 'ai_prompts', 'update', 'tenant', 'Activate reviewed prompts', true),

    -- Embeddings and KBs
    ('ai.embeddings.read', ai_product_id, 'embeddings', 'read', 'tenant', 'Query embeddings (search)', false),
    ('ai.kb.manage', ai_product_id, 'ai_knowledge_bases', 'update', 'tenant', 'Manage knowledge bases', false),

    -- Document extraction
    ('ai.documents.extract', ai_product_id, 'ai_document_extractions', 'create', 'tenant', 'Extract data from documents', false),
    ('ai.documents.review', ai_product_id, 'ai_document_extractions', 'update', 'tenant', 'Review AI extractions', false),

    -- Predictions
    ('ai.predictions.read', ai_product_id, 'ai_predictions', 'read', 'tenant', 'View ML predictions', false),

    -- Feedback
    ('ai.feedback.create', ai_product_id, 'ai_feedback', 'create', 'tenant', 'Submit feedback on AI outputs', false);

    -- Assign AI permissions to tenant roles
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.product_id = ai_product_id
      AND p.is_dangerous = false;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'doctor'
      AND p.permission_key IN ('ai.workflows.execute', 'ai.runs.read', 'ai.documents.extract', 'ai.documents.review', 'ai.feedback.create', 'ai.predictions.read', 'ai.embeddings.read');
END $$;

-- ============================================================================
-- VIEWS for AI observability
-- ============================================================================

-- Recent AI activity per tenant
CREATE OR REPLACE VIEW ai.v_recent_activity AS
SELECT
    r.tenant_id,
    t.display_name AS tenant_name,
    w.workflow_key,
    r.status,
    r.started_at,
    r.duration_ms,
    r.total_cost_usd,
    r.confidence_score
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
JOIN platform.tenants t ON t.tenant_id = r.tenant_id
WHERE r.started_at >= NOW() - INTERVAL '24 hours'
ORDER BY r.started_at DESC;

-- Cost summary per tenant per use case
CREATE OR REPLACE VIEW ai.v_cost_summary AS
SELECT
    r.tenant_id,
    w.use_case,
    DATE_TRUNC('day', r.started_at) AS day,
    COUNT(*) AS run_count,
    SUM(r.total_cost_usd) AS total_cost_usd,
    AVG(r.duration_ms) AS avg_duration_ms,
    COUNT(*) FILTER (WHERE r.status = 'success') AS success_count,
    COUNT(*) FILTER (WHERE r.status = 'failed') AS failed_count
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
WHERE r.started_at >= NOW() - INTERVAL '30 days'
GROUP BY r.tenant_id, w.use_case, DATE_TRUNC('day', r.started_at);

-- Workflows needing human attention
CREATE OR REPLACE VIEW ai.v_human_review_queue AS
SELECT
    'workflow_approval'::TEXT AS source,
    r.run_id AS item_id,
    r.tenant_id,
    w.workflow_key,
    r.started_at AS created_at,
    r.input_data,
    r.output_data,
    'Workflow awaiting approval'::TEXT AS reason
FROM ai.ai_agent_runs r
JOIN ai.ai_workflows w ON w.workflow_id = r.workflow_id
WHERE r.status = 'awaiting_human'

UNION ALL

SELECT
    'document_extraction'::TEXT AS source,
    de.extraction_id AS item_id,
    de.tenant_id,
    de.source_type AS workflow_key,
    de.created_at,
    NULL::JSONB AS input_data,
    de.extracted_data AS output_data,
    ('Document extraction confidence: ' || COALESCE(de.overall_confidence::TEXT, 'n/a'))::TEXT AS reason
FROM ai.ai_document_extractions de
WHERE de.requires_human_review = true AND de.reviewed_at IS NULL

ORDER BY created_at DESC;

-- ============================================================================
-- SEED: Default AI workflows (platform-wide, can be overridden per tenant)
-- ============================================================================

INSERT INTO ai.ai_workflows (workflow_key, name, description, use_case, graph_definition, requires_human_approval) VALUES
('symptom_triage_v1', 'Patient Symptom Triage', 'Classifies WhatsApp symptom messages into urgency tiers and routes to appropriate doctor specialization', 'triage',
'{"nodes": ["classify_intent", "assess_severity", "check_red_flags", "recommend_specialty", "format_response"], "edges": [["classify_intent", "assess_severity"], ["assess_severity", "check_red_flags"], ["check_red_flags", "recommend_specialty"], ["recommend_specialty", "format_response"]], "conditional_edges": [{"from": "check_red_flags", "condition": "is_emergency", "to": "emergency_escalation"}]}',
false),

('prescription_safety_check_v1', 'Prescription Safety Review', 'Cross-checks new prescription against patient allergies, current medications, and drug interactions', 'prescription_review',
'{"nodes": ["fetch_patient_history", "check_allergies", "check_interactions", "check_dosing", "generate_warnings"], "edges": [["fetch_patient_history", "check_allergies"], ["check_allergies", "check_interactions"], ["check_interactions", "check_dosing"], ["check_dosing", "generate_warnings"]]}',
true),

('medical_history_rag_v1', 'Patient Medical History Q&A', 'Doctor queries patient history in natural language; RAG retrieves relevant records with consent check', 'rag_medical',
'{"nodes": ["verify_consent", "retrieve_relevant_records", "decrypt_chunks", "synthesize_answer", "cite_sources"], "edges": [["verify_consent", "retrieve_relevant_records"], ["retrieve_relevant_records", "decrypt_chunks"], ["decrypt_chunks", "synthesize_answer"], ["synthesize_answer", "cite_sources"]]}',
false),

('lab_report_extraction_v1', 'Lab Report OCR & Extraction', 'Extracts structured test values from uploaded PDF/image lab reports', 'ocr_lab_report',
'{"nodes": ["ocr_pages", "detect_test_panel", "extract_values", "normalize_units", "flag_abnormal"], "edges": [["ocr_pages", "detect_test_panel"], ["detect_test_panel", "extract_values"], ["extract_values", "normalize_units"], ["normalize_units", "flag_abnormal"]]}',
true);

-- ============================================================================
-- AI DATA-PLANE RLS (Phase-0 gap fix) — tenant isolation on the PHI-bearing ai.*
-- tables, mirroring the PHI pattern in 05 (active tenant OR scoped impersonation;
-- the super_admin god-flag is intentionally NOT honored). Defense-in-depth:
-- TODAY the Python AI service connects as the DB owner (RLS-bypassing) and filters
-- by tenant in code, so these policies are a BACKSTOP for the .NET app role
-- (docslot_app, NOBYPASSRLS) and for the intended future state where the AI service
-- connects as a least-privilege role and SET LOCAL app.tenant_id per request
-- (see db/rls.py in the architecture note below).
-- ============================================================================
ALTER TABLE ai.embeddings              ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_predictions          ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_agent_runs           ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_document_extractions ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai.ai_agent_steps          ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_embeddings ON ai.embeddings
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_predictions ON ai.ai_predictions
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_agent_runs ON ai.ai_agent_runs
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

CREATE POLICY tenant_isolation_ai_document_extractions ON ai.ai_document_extractions
    FOR ALL
    USING (tenant_id = platform.current_tenant_id()
           OR tenant_id = platform.current_impersonated_tenant());

-- ai_agent_steps has no tenant_id; gate via the parent run.
CREATE POLICY tenant_isolation_ai_agent_steps ON ai.ai_agent_steps
    FOR ALL
    USING (
        EXISTS (
            SELECT 1 FROM ai.ai_agent_runs r
            WHERE r.run_id = ai_agent_steps.run_id
              AND (r.tenant_id = platform.current_tenant_id()
                   OR r.tenant_id = platform.current_impersonated_tenant())
        )
    );

-- ============================================================================
-- END OF AI SERVICES SCHEMA
-- ============================================================================
-- Tables: 10 (AI1-AI10)
-- Indexes: ~25
-- Views: 3
-- Permissions: 17 (registered in platform.permissions)
-- Workflows seeded: 4 (symptom_triage, prescription_safety, medical_history_rag, lab_report_extraction)
--
-- TOTAL DATABASE TABLES: 97 (was 87)
--
-- WHAT THE PYTHON SERVICE LOOKS LIKE (architecture, not in this SQL):
--
-- docslot-ai-service/
--   pyproject.toml          # FastAPI + LangGraph + LangChain + pandas
--   src/
--     api/
--       fastapi_app.py      # FastAPI with JWT auth (validates platform.user_sessions)
--       routes/             # /triage, /prescription-review, /rag-query, /extract-document
--     workflows/            # LangGraph workflow implementations
--       triage_graph.py     # Loaded from ai.ai_workflows table
--       prescription_safety.py
--       medical_rag.py
--     agents/               # Multi-agent orchestrators
--     embeddings/           # Embedding generation + retrieval
--     ocr/                  # PaddleOCR / Tesseract / Vision API pipelines
--     analytics/            # pandas + scikit-learn for predictions
--       no_show_model.py
--       demand_forecast.py
--     db/
--       session.py          # SQLAlchemy async sessions, same DB as .NET
--       audit.py            # Writes to platform.audit_log
--       rls.py              # Sets SET LOCAL app.tenant_id on every request
--     security/
--       kms.py              # Same KMS provider as .NET, shared encryption_keys
--   tests/
--   docker/
--     Dockerfile            # python:3.12-slim
--   k8s/
--     deployment.yaml       # Separate scaling from .NET
-- ============================================================================
