-- ============================================================================
-- DocSlot Platform Database — Part 3: DocSlot Product Schema
-- ============================================================================
-- This is the DocSlot product (appointment booking for healthcare).
-- All tables scoped by tenant_id (which IS the healthcare facility).
-- Depends on: platform.* and platform_api.* schemas
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS docslot;
COMMENT ON SCHEMA docslot IS 'DocSlot appointment booking product. All tables scoped by tenant_id.';

-- ============================================================================
-- TABLE D1: HEALTHCARE_FACILITIES (extends tenant with healthcare-specific data)
-- ============================================================================
-- Rather than overloading platform.tenants, we extend it with a 1:1 table
-- containing healthcare-specific attributes. Each tenant subscribed to DocSlot
-- gets one row here.
CREATE TABLE docslot.healthcare_facilities (
    facility_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL UNIQUE REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,

    facility_type       VARCHAR(30) NOT NULL CHECK (facility_type IN ('individual_doctor', 'hospital', 'pathology_lab', 'clinic', 'pharmacy', 'diagnostic_center')),
    specialty_focus     VARCHAR(100),

    -- WhatsApp Business config
    whatsapp_business_phone_id VARCHAR(50),
    whatsapp_access_token TEXT,                                -- Encrypted
    whatsapp_verified_at TIMESTAMPTZ,

    -- ABDM/India regulatory
    hfr_id              VARCHAR(50),                            -- Health Facility Registry ID
    hfr_status          VARCHAR(20) DEFAULT 'not_registered'
        CHECK (hfr_status IN ('not_registered', 'pending', 'verified', 'rejected', 'suspended')),

    -- Operational config
    appointment_settings JSONB NOT NULL DEFAULT '{}',         -- slot_duration_minutes, auto_confirm, etc.
    business_hours      JSONB NOT NULL DEFAULT '{}',         -- Per-day open/close times

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_facilities_type ON docslot.healthcare_facilities(facility_type);
CREATE INDEX idx_facilities_hfr ON docslot.healthcare_facilities(hfr_id) WHERE hfr_id IS NOT NULL;

CREATE TRIGGER trg_facilities_updated_at BEFORE UPDATE ON docslot.healthcare_facilities
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D2: DEPARTMENTS (for hospitals)
-- ============================================================================
CREATE TABLE docslot.departments (
    department_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    name                VARCHAR(100) NOT NULL,
    code                VARCHAR(20),
    description         TEXT,
    icon                VARCHAR(50),
    display_order       SMALLINT NOT NULL DEFAULT 0,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(tenant_id, name)
);

CREATE INDEX idx_departments_tenant ON docslot.departments(tenant_id, display_order) WHERE is_active = true;

-- ============================================================================
-- TABLE D3: DOCTORS (healthcare providers — also covers lab technicians)
-- ============================================================================
CREATE TABLE docslot.doctors (
    doctor_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),  -- Links to platform user if doctor logs in

    -- Identity
    full_name           VARCHAR(200) NOT NULL,
    display_name        VARCHAR(200),
    gender              VARCHAR(10) CHECK (gender IS NULL OR gender IN ('male', 'female', 'other', 'prefer_not_say')),
    profile_image_url   TEXT,

    -- Professional
    department_id       UUID REFERENCES docslot.departments(department_id),
    role                VARCHAR(30) NOT NULL DEFAULT 'doctor' CHECK (role IN ('doctor', 'technician', 'nurse', 'pharmacist')),
    specialization      VARCHAR(100),
    sub_specialization  VARCHAR(100),
    qualifications      JSONB NOT NULL DEFAULT '[]',              -- [{degree, college, year}]
    experience_years    SMALLINT,
    languages_spoken    VARCHAR(10)[] DEFAULT ARRAY['en'],
    biography           TEXT,

    -- Pricing
    consultation_fee    DECIMAL(10,2),
    follow_up_fee       DECIMAL(10,2),

    -- Contact
    phone               VARCHAR(15),
    email               CITEXT,

    -- Indian regulatory (NMC/HPR)
    nmc_registration_number VARCHAR(50),
    nmc_state_council   VARCHAR(50),
    nmc_registration_year INT,
    nmc_expires_at      DATE,
    hpr_id              VARCHAR(50),
    nmc_verification_status VARCHAR(20) DEFAULT 'not_verified'
        CHECK (nmc_verification_status IN ('not_verified', 'pending', 'verified', 'rejected', 'expired')),
    nmc_verified_at     TIMESTAMPTZ,

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_accepting_new_patients BOOLEAN NOT NULL DEFAULT true,

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX idx_doctors_tenant ON docslot.doctors(tenant_id) WHERE deleted_at IS NULL AND is_active = true;
CREATE INDEX idx_doctors_department ON docslot.doctors(department_id) WHERE deleted_at IS NULL;
CREATE INDEX idx_doctors_specialization ON docslot.doctors(tenant_id, specialization) WHERE deleted_at IS NULL;
CREATE INDEX idx_doctors_nmc ON docslot.doctors(nmc_registration_number) WHERE nmc_registration_number IS NOT NULL;
CREATE INDEX idx_doctors_user ON docslot.doctors(user_id) WHERE user_id IS NOT NULL;
CREATE INDEX idx_doctors_search ON docslot.doctors USING gin(full_name gin_trgm_ops) WHERE deleted_at IS NULL;

CREATE TRIGGER trg_doctors_updated_at BEFORE UPDATE ON docslot.doctors
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D4: DOCTOR_SCHEDULES (recurring weekly availability)
-- ============================================================================
CREATE TABLE docslot.doctor_schedules (
    schedule_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    day_of_week         SMALLINT NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time          TIME NOT NULL,
    end_time            TIME NOT NULL,
    slot_duration_minutes SMALLINT NOT NULL DEFAULT 15,
    max_patients_per_slot SMALLINT NOT NULL DEFAULT 1,
    break_start_time    TIME,
    break_end_time      TIME,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT chk_schedule_time CHECK (end_time > start_time),
    CONSTRAINT chk_break_time CHECK (
        (break_start_time IS NULL AND break_end_time IS NULL) OR
        (break_start_time IS NOT NULL AND break_end_time IS NOT NULL AND break_end_time > break_start_time)
    )
);

CREATE INDEX idx_schedules_doctor ON docslot.doctor_schedules(doctor_id) WHERE is_active = true;

-- ============================================================================
-- TABLE D5: SCHEDULE_OVERRIDES (holidays, leaves, special hours)
-- ============================================================================
CREATE TABLE docslot.schedule_overrides (
    override_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    override_date       DATE NOT NULL,
    is_blocked          BOOLEAN NOT NULL DEFAULT true,
    custom_start_time   TIME,
    custom_end_time     TIME,
    reason              VARCHAR(200),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(doctor_id, override_date)
);

-- ============================================================================
-- TABLE D6: TIME_SLOTS (generated bookable slots)
-- ============================================================================
CREATE TABLE docslot.time_slots (
    slot_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id) ON DELETE CASCADE,
    slot_date           DATE NOT NULL,
    start_time          TIME NOT NULL,
    end_time            TIME NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'available' CHECK (status IN ('available', 'booked', 'blocked')),
    current_count       SMALLINT NOT NULL DEFAULT 0,
    max_count           SMALLINT NOT NULL DEFAULT 1,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(doctor_id, slot_date, start_time)
);

CREATE INDEX idx_slots_available ON docslot.time_slots(doctor_id, slot_date, status) WHERE status = 'available';
CREATE INDEX idx_slots_tenant_date ON docslot.time_slots(tenant_id, slot_date);

-- ----------------------------------------------------------------------------
-- TABLE D6b: SLOT_HOLDS (hold-on-selection with TTL — FR-BOOK-02)
-- ----------------------------------------------------------------------------
-- When a patient/staff selects a slot, it is held for ~5 minutes so a concurrent
-- booker can't double-book while the form is filled. Abandoned holds expire and
-- free the slot. A booking confirmation converts the hold (consumes capacity).
-- (Promoted from an app-owned runtime table in slice 05 so the app no longer
-- issues DDL; the substrate owns the schema.)
CREATE TABLE docslot.slot_holds (
    hold_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    slot_id             UUID NOT NULL REFERENCES docslot.time_slots(slot_id) ON DELETE CASCADE,
    hold_token          VARCHAR(64) NOT NULL,                 -- opaque token returned to the holder
    booking_id          UUID,                                 -- set when the hold is converted to a booking
    status              VARCHAR(20) NOT NULL DEFAULT 'held'
        CHECK (status IN ('held', 'converted', 'released', 'expired')),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL                  -- created_at + TTL (app sets 5 min)
);

-- tenant_id-leading composite; partial index for the live-hold concurrency check.
CREATE INDEX idx_slot_holds_tenant_live ON docslot.slot_holds(tenant_id, slot_id) WHERE status = 'held';
CREATE INDEX idx_slot_holds_live ON docslot.slot_holds(slot_id) WHERE status = 'held';
CREATE INDEX idx_slot_holds_expiry ON docslot.slot_holds(expires_at) WHERE status = 'held';

COMMENT ON TABLE docslot.slot_holds IS 'Transient TTL holds on time_slots (FR-BOOK-02). A sweeper expires stale holds; confirmed bookings convert them.';

-- ============================================================================
-- TABLE D7: PATIENTS (cross-tenant patient identity)
-- ============================================================================
-- Patients exist at platform level (one phone = one patient identity)
-- but visit multiple tenants. ABHA ID enables cross-tenant medical records.
CREATE TABLE docslot.patients (
    patient_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Primary identity
    phone_number        VARCHAR(15) NOT NULL UNIQUE,
    whatsapp_id         VARCHAR(50),

    -- Demographics
    full_name           VARCHAR(200),
    date_of_birth       DATE,
    age                 SMALLINT,                              -- Computed from DOB or self-reported
    gender              VARCHAR(10) CHECK (gender IS NULL OR gender IN ('male', 'female', 'other', 'prefer_not_say')),
    blood_group         VARCHAR(5) CHECK (blood_group IS NULL OR blood_group IN ('A+','A-','B+','B-','AB+','AB-','O+','O-','UNK')),

    -- Contact
    email               CITEXT,
    address_line1       VARCHAR(200),
    city                VARCHAR(100),
    state               VARCHAR(100),
    pin_code            VARCHAR(10),
    country             VARCHAR(2) NOT NULL DEFAULT 'IN',

    -- Emergency contact
    emergency_contact_name VARCHAR(200),
    emergency_contact_phone VARCHAR(15),
    emergency_contact_relationship VARCHAR(50),

    -- Indian identity (DPDP-compliant — store last 4 only). ENCRYPTED at rest (envelope) → TEXT, not VARCHAR(4).
    aadhaar_last_4      TEXT,

    -- Preferences
    preferred_language  VARCHAR(10) NOT NULL DEFAULT 'en',
    preferred_communication VARCHAR(20) DEFAULT 'whatsapp' CHECK (preferred_communication IN ('whatsapp', 'sms', 'email', 'voice_call')),

    -- DPDP consent
    consent_given_at    TIMESTAMPTZ,
    consent_version     VARCHAR(20),
    consent_ip_address  INET,
    data_retention_until TIMESTAMPTZ,
    deletion_requested_at TIMESTAMPTZ,
    last_incoming_message_at TIMESTAMPTZ,                      -- For 24h WhatsApp window tracking

    -- Status
    is_active           BOOLEAN NOT NULL DEFAULT true,

    -- Audit
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at          TIMESTAMPTZ
);

CREATE INDEX idx_patients_phone ON docslot.patients(phone_number) WHERE deleted_at IS NULL;
CREATE INDEX idx_patients_whatsapp ON docslot.patients(whatsapp_id) WHERE whatsapp_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX idx_patients_name_search ON docslot.patients USING gin(full_name gin_trgm_ops) WHERE deleted_at IS NULL;

CREATE TRIGGER trg_patients_updated_at BEFORE UPDATE ON docslot.patients
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D8: PATIENT_TENANT_LINKS (which tenants has this patient visited)
-- ============================================================================
-- Tracks the relationship between a patient and the healthcare facilities they've visited.
-- This enables cross-tenant features while respecting tenant data isolation.
CREATE TABLE docslot.patient_tenant_links (
    link_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    patient_local_id    VARCHAR(50),                            -- Tenant's internal patient ID (MRN)
    first_visit_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_visit_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    total_visits        INT NOT NULL DEFAULT 0,
    tenant_notes        TEXT,
    UNIQUE(patient_id, tenant_id)
);

CREATE INDEX idx_patient_tenant ON docslot.patient_tenant_links(tenant_id, patient_id);

-- ============================================================================
-- TABLE D9: FAMILY_MEMBERS (multi-patient under one phone)
-- ============================================================================
CREATE TABLE docslot.family_members (
    family_member_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    primary_patient_id  UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    member_patient_id   UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    relationship        VARCHAR(50) NOT NULL CHECK (relationship IN ('spouse','child','parent','sibling','grandparent','grandchild','other')),
    is_primary_decision_maker BOOLEAN NOT NULL DEFAULT false,
    added_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(primary_patient_id, member_patient_id),
    CHECK (primary_patient_id != member_patient_id)
);

CREATE INDEX idx_family_primary ON docslot.family_members(primary_patient_id);

-- ============================================================================
-- TABLE D10: BOOKINGS (the core entity)
-- ============================================================================
CREATE TABLE docslot.bookings (
    booking_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_number      VARCHAR(20) NOT NULL UNIQUE,            -- Human-readable: 'BKG-2026-04-00001'
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    slot_id             UUID NOT NULL REFERENCES docslot.time_slots(slot_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    department_id       UUID REFERENCES docslot.departments(department_id),

    -- Booking details
    booking_type        VARCHAR(20) NOT NULL DEFAULT 'consultation'
        CHECK (booking_type IN ('consultation', 'follow_up', 'test', 'home_collection', 'procedure', 'tele_consultation')),
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'confirmed', 'checked_in', 'cancelled', 'completed', 'no_show', 'rescheduled')),

    -- Patient info snapshot (in case patient data changes later)
    patient_name_at_booking VARCHAR(200),
    patient_phone_at_booking VARCHAR(15),
    patient_age_at_booking SMALLINT,

    -- Booking context
    booked_via          VARCHAR(20) NOT NULL DEFAULT 'whatsapp'
        CHECK (booked_via IN ('whatsapp', 'dashboard', 'api', 'walk_in', 'phone_call')),
    booked_for          VARCHAR(20) NOT NULL DEFAULT 'self'
        CHECK (booked_for IN ('self', 'family_member', 'other')),
    booked_for_patient_id UUID REFERENCES docslot.patients(patient_id),

    -- Notes
    chief_complaint     TEXT,                                    -- Why patient wants to visit
    notes               TEXT,
    cancellation_reason TEXT,

    -- Reminders
    reminder_24h_sent   BOOLEAN NOT NULL DEFAULT false,
    reminder_1h_sent    BOOLEAN NOT NULL DEFAULT false,

    -- Timestamps
    booked_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    confirmed_at        TIMESTAMPTZ,
    checked_in_at       TIMESTAMPTZ,                             -- front-desk arrival (confirmed → checked_in)
    cancelled_at        TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    no_show_at          TIMESTAMPTZ,
    rescheduled_at      TIMESTAMPTZ,                             -- when this booking was superseded by a reschedule

    -- Reschedule lineage: a reschedule TERMINATES this row (status 'rescheduled') and mints a NEW booking
    -- on the new slot. The new row points back here so the move is auditable end-to-end.
    rescheduled_from_booking_id UUID REFERENCES docslot.bookings(booking_id),

    -- Audit
    created_by_user_id  UUID REFERENCES platform.users(user_id),
    cancelled_by_user_id UUID REFERENCES platform.users(user_id),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_bookings_tenant_status ON docslot.bookings(tenant_id, status);
CREATE INDEX idx_bookings_patient ON docslot.bookings(patient_id, booked_at DESC);
CREATE INDEX idx_bookings_doctor_date ON docslot.bookings(doctor_id, booked_at);
CREATE INDEX idx_bookings_pending ON docslot.bookings(tenant_id) WHERE status = 'pending';
CREATE INDEX idx_bookings_reminders ON docslot.bookings(status)
    WHERE status = 'confirmed' AND (reminder_24h_sent = false OR reminder_1h_sent = false);
CREATE INDEX idx_bookings_number ON docslot.bookings(booking_number);

CREATE TRIGGER trg_bookings_updated_at BEFORE UPDATE ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- ============================================================================
-- TABLE D11: BOOKING_STATUS_HISTORY (state transition audit)
-- ============================================================================
CREATE TABLE docslot.booking_status_history (
    history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id) ON DELETE CASCADE,
    from_status         VARCHAR(20),
    to_status           VARCHAR(20) NOT NULL,
    changed_by_user_id  UUID REFERENCES platform.users(user_id),
    changed_via         VARCHAR(20),
    reason              TEXT,
    metadata            JSONB DEFAULT '{}',
    changed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_booking_history ON docslot.booking_status_history(booking_id, changed_at DESC);

-- ============================================================================
-- TABLE D12: OPD_TOKENS (queue management for hospital OPD)
-- ============================================================================
CREATE TABLE docslot.opd_tokens (
    token_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL UNIQUE REFERENCES docslot.bookings(booking_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    token_date          DATE NOT NULL,
    token_number        INT NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'waiting'
        CHECK (status IN ('waiting', 'called', 'in_consultation', 'completed', 'skipped', 'no_show')),
    called_at           TIMESTAMPTZ,
    consultation_started_at TIMESTAMPTZ,
    completed_at        TIMESTAMPTZ,
    estimated_wait_minutes INT,
    UNIQUE(doctor_id, token_date, token_number)
);

CREATE INDEX idx_tokens_active ON docslot.opd_tokens(doctor_id, token_date, token_number)
    WHERE status IN ('waiting', 'called', 'in_consultation');

-- ============================================================================
-- TABLE D13: TEST_CATALOG (for pathology labs)
-- ============================================================================
CREATE TABLE docslot.test_catalog (
    test_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    test_name           VARCHAR(200) NOT NULL,
    test_code           VARCHAR(50),
    category            VARCHAR(50),
    description         TEXT,
    sample_type         VARCHAR(50),                            -- 'blood', 'urine', 'stool'
    preparation_instructions TEXT,
    price               DECIMAL(10,2),
    discount_price      DECIMAL(10,2),
    report_turnaround_hours SMALLINT,
    is_home_collection_available BOOLEAN NOT NULL DEFAULT false,
    home_collection_fee DECIMAL(10,2),
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_test_catalog_tenant ON docslot.test_catalog(tenant_id, category) WHERE is_active = true;
CREATE INDEX idx_test_catalog_search ON docslot.test_catalog USING gin(test_name gin_trgm_ops) WHERE is_active = true;

-- ============================================================================
-- TABLE D14: PROCEDURE_CATALOG (for hospitals — surgical/IPD procedures)
-- ============================================================================
CREATE TABLE docslot.procedure_catalog (
    procedure_id        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    department_id       UUID REFERENCES docslot.departments(department_id),
    procedure_name      VARCHAR(200) NOT NULL,
    procedure_code      VARCHAR(50),
    category            VARCHAR(50) NOT NULL CHECK (category IN ('surgery', 'admission', 'day_care', 'icu_stay', 'diagnostic_procedure')),
    description         TEXT,
    typical_duration_hours DECIMAL(5,2),

    -- Itemized pricing (transparency requirement)
    base_price          DECIMAL(10,2) NOT NULL,
    doctor_fee          DECIMAL(10,2) NOT NULL DEFAULT 0,
    anesthesia_fee      DECIMAL(10,2) NOT NULL DEFAULT 0,
    consumables_estimate DECIMAL(10,2) NOT NULL DEFAULT 0,
    room_charges_per_day DECIMAL(10,2) NOT NULL DEFAULT 0,
    minimum_stay_days   SMALLINT NOT NULL DEFAULT 0,

    estimated_min_total DECIMAL(10,2) NOT NULL,
    estimated_max_total DECIMAL(10,2) NOT NULL,

    inclusions          JSONB NOT NULL DEFAULT '[]',
    exclusions          JSONB NOT NULL DEFAULT '[]',

    -- Insurance
    cashless_eligible   BOOLEAN NOT NULL DEFAULT false,
    ab_pmjay_covered    BOOLEAN NOT NULL DEFAULT false,
    ab_pmjay_package_code VARCHAR(20),

    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_procedures_tenant ON docslot.procedure_catalog(tenant_id, category) WHERE is_active = true;
CREATE INDEX idx_procedures_pmjay ON docslot.procedure_catalog(ab_pmjay_package_code) WHERE ab_pmjay_covered = true;

-- ============================================================================
-- TABLE D15: PATIENT_MEDICAL_HISTORY (allergies, conditions, medications)
-- ============================================================================
CREATE TABLE docslot.patient_medical_history (
    history_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id) ON DELETE CASCADE,
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    record_type         VARCHAR(30) NOT NULL CHECK (record_type IN ('allergy', 'chronic_condition', 'surgery', 'medication', 'vaccination', 'family_history', 'lifestyle')),
    title               TEXT NOT NULL,                          -- ENCRYPTED at rest (envelope); registered in encrypted_fields_registry → must be TEXT, not VARCHAR(200)
    description         TEXT,                                   -- ENCRYPTED at rest (envelope)
    started_date        DATE,
    ended_date          DATE,
    severity            VARCHAR(20) CHECK (severity IS NULL OR severity IN ('mild', 'moderate', 'severe', 'critical')),
    icd10_code          VARCHAR(10),                            -- For standardized coding
    is_active           BOOLEAN NOT NULL DEFAULT true,
    is_critical         BOOLEAN NOT NULL DEFAULT false,         -- Flagged for safety alerts
    added_by_user_id    UUID REFERENCES platform.users(user_id),
    added_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_medical_history_patient ON docslot.patient_medical_history(patient_id, record_type) WHERE is_active = true;
CREATE INDEX idx_medical_history_critical ON docslot.patient_medical_history(patient_id) WHERE is_critical = true;

-- ============================================================================
-- TABLE D16: PRESCRIPTIONS
-- ============================================================================
CREATE TABLE docslot.prescriptions (
    prescription_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_number VARCHAR(30) NOT NULL UNIQUE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Clinical content
    chief_complaints    TEXT,
    examination         TEXT,
    diagnosis           TEXT,
    medications         JSONB NOT NULL DEFAULT '[]',
    investigations      JSONB DEFAULT '[]',                     -- Tests ordered
    advice              TEXT,
    follow_up_in_days   INT,

    -- Generated artifacts
    pdf_url             TEXT,
    file_name           VARCHAR(200),

    -- Delivery
    status              VARCHAR(20) NOT NULL DEFAULT 'draft'
        CHECK (status IN ('draft', 'finalized', 'delivered', 'amended')),
    delivered_at        TIMESTAMPTZ,
    delivery_message_id VARCHAR(100),

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_prescriptions_patient ON docslot.prescriptions(patient_id, created_at DESC);
CREATE INDEX idx_prescriptions_booking ON docslot.prescriptions(booking_id);

-- ============================================================================
-- TABLE D17: DRUG_ALERTS (allergies/interactions flagged at prescription time)
-- ============================================================================
CREATE TABLE docslot.drug_alerts (
    alert_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_id     UUID NOT NULL REFERENCES docslot.prescriptions(prescription_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    alert_type          VARCHAR(30) NOT NULL CHECK (alert_type IN ('allergy', 'interaction', 'contraindication', 'duplicate', 'pregnancy_warning', 'dosage')),
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('low', 'moderate', 'high', 'critical')),
    medication_name     VARCHAR(200) NOT NULL,
    conflicting_record_id UUID,
    description         TEXT NOT NULL,
    overridden          BOOLEAN NOT NULL DEFAULT false,
    overridden_by_user_id UUID REFERENCES platform.users(user_id),
    override_reason     TEXT,
    overridden_at       TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_drug_alerts_critical ON docslot.drug_alerts(patient_id) WHERE severity = 'critical' AND NOT overridden;

-- ============================================================================
-- TABLE D18: LAB_REPORTS
-- ============================================================================
CREATE TABLE docslot.lab_reports (
    report_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    report_number       VARCHAR(30) NOT NULL UNIQUE,
    booking_id          UUID NOT NULL REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    test_id             UUID REFERENCES docslot.test_catalog(test_id),

    file_url            TEXT,
    file_name           VARCHAR(200),
    file_size_bytes     BIGINT,
    file_mime_type      VARCHAR(100),

    -- Structured results (in addition to PDF)
    structured_results  JSONB,                                   -- {parameter: value, normal_range, status}

    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'ready', 'delivered', 'cancelled')),
    uploaded_at         TIMESTAMPTZ,
    uploaded_by_user_id UUID REFERENCES platform.users(user_id),
    delivered_at        TIMESTAMPTZ,
    delivery_message_id VARCHAR(100),

    -- Critical findings flag
    has_critical_findings BOOLEAN NOT NULL DEFAULT false,
    critical_findings_notified_at TIMESTAMPTZ,

    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_reports_patient ON docslot.lab_reports(patient_id, created_at DESC);
CREATE INDEX idx_reports_ready ON docslot.lab_reports(tenant_id, status) WHERE status = 'ready';
CREATE INDEX idx_reports_critical ON docslot.lab_reports(tenant_id) WHERE has_critical_findings = true AND critical_findings_notified_at IS NULL;


-- ============================================================================
-- TABLE D19: CONVERSATIONS (WhatsApp conversation state)
-- ============================================================================
CREATE TABLE docslot.conversations (
    conversation_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    whatsapp_phone      VARCHAR(15) NOT NULL,
    current_step        VARCHAR(50) NOT NULL DEFAULT 'greeting',
    context             JSONB NOT NULL DEFAULT '{}',
    detected_language   VARCHAR(10),
    last_message_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '30 minutes'),
    is_active           BOOLEAN NOT NULL DEFAULT true
);

CREATE INDEX idx_conversations_active ON docslot.conversations(whatsapp_phone, tenant_id) WHERE is_active = true;
CREATE INDEX idx_conversations_expired ON docslot.conversations(expires_at) WHERE is_active = true;

-- ============================================================================
-- TABLE D20: WA_MESSAGE_LOG (WhatsApp message tracking)
-- ============================================================================
CREATE TABLE docslot.wa_message_log (
    log_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    conversation_id     UUID REFERENCES docslot.conversations(conversation_id),
    whatsapp_message_id VARCHAR(100),
    direction           VARCHAR(10) NOT NULL CHECK (direction IN ('inbound', 'outbound')),
    message_type        VARCHAR(20) NOT NULL,                  -- 'text', 'template', 'interactive', 'audio', 'image', 'document'
    template_name       VARCHAR(100),
    content             JSONB,
    -- Canonical message-log status vocabulary across BOTH legs:
    --   inbound  : 'received'
    --   outbound : 'queued' → 'sent' → 'delivered' → 'read'  (or 'failed' on any leg)
    status              VARCHAR(20)
        CHECK (status IS NULL OR status IN ('received', 'queued', 'sent', 'delivered', 'read', 'failed')),
    error_code          VARCHAR(50),
    cost_usd            DECIMAL(10,4),
    sent_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    delivered_at        TIMESTAMPTZ,
    read_at             TIMESTAMPTZ,
    failed_at           TIMESTAMPTZ
);

CREATE INDEX idx_wa_log_tenant ON docslot.wa_message_log(tenant_id, sent_at DESC);
CREATE INDEX idx_wa_log_failed ON docslot.wa_message_log(tenant_id) WHERE status = 'failed';
CREATE INDEX idx_wa_log_message_id ON docslot.wa_message_log(whatsapp_message_id) WHERE whatsapp_message_id IS NOT NULL;

-- ============================================================================
-- TABLE D21: OUTBOX_MESSAGES (reliable WhatsApp delivery queue)
-- ============================================================================
CREATE TABLE docslot.outbox_messages (
    outbox_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID REFERENCES docslot.patients(patient_id),
    message_intent      VARCHAR(50) NOT NULL,
    payload             JSONB NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'sent', 'failed', 'abandoned')),
    attempt_count       SMALLINT NOT NULL DEFAULT 0,
    max_attempts        SMALLINT NOT NULL DEFAULT 5,
    last_error          TEXT,
    next_retry_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ,
    whatsapp_message_id VARCHAR(100),
    correlation_id      VARCHAR(100),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_outbox_pending ON docslot.outbox_messages(next_retry_at, status) WHERE status IN ('pending', 'failed');

-- ============================================================================
-- TABLE D22: PROCESSED_MESSAGES (webhook idempotency)
-- ============================================================================
CREATE TABLE docslot.processed_messages (
    whatsapp_message_id VARCHAR(100) PRIMARY KEY,
    processed_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_processed_recent ON docslot.processed_messages(processed_at DESC);

-- ============================================================================
-- TABLE D23: WAITLIST
-- ============================================================================
CREATE TABLE docslot.waitlist (
    waitlist_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    requested_date      DATE NOT NULL,
    requested_time_range VARCHAR(20),
    status              VARCHAR(20) NOT NULL DEFAULT 'waiting'
        CHECK (status IN ('waiting', 'notified', 'booked', 'expired', 'cancelled')),
    notified_at         TIMESTAMPTZ,
    expires_at          TIMESTAMPTZ NOT NULL DEFAULT (NOW() + INTERVAL '7 days'),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_waitlist_active ON docslot.waitlist(doctor_id, requested_date) WHERE status = 'waiting';

-- ============================================================================
-- TABLE D24: REVIEWS (patient ratings of doctors)
-- ============================================================================
CREATE TABLE docslot.reviews (
    review_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id          UUID NOT NULL UNIQUE REFERENCES docslot.bookings(booking_id),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    doctor_id           UUID NOT NULL REFERENCES docslot.doctors(doctor_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    rating              SMALLINT NOT NULL CHECK (rating BETWEEN 1 AND 5),
    comment             TEXT,
    aspects             JSONB DEFAULT '{}',
    is_anonymous        BOOLEAN NOT NULL DEFAULT false,
    is_verified         BOOLEAN NOT NULL DEFAULT true,
    is_published        BOOLEAN NOT NULL DEFAULT true,
    response_from_doctor TEXT,
    responded_at        TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_reviews_doctor_published ON docslot.reviews(doctor_id, created_at DESC) WHERE is_published = true;

-- ============================================================================
-- TABLE D25: ABDM_HEALTH_RECORDS (FHIR R4 health records)
-- ============================================================================
CREATE TABLE docslot.abdm_health_records (
    record_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    booking_id          UUID REFERENCES docslot.bookings(booking_id),
    abha_number         VARCHAR(20) NOT NULL,
    record_type         VARCHAR(50) NOT NULL CHECK (record_type IN ('OPConsultation', 'DischargeSummary', 'Prescription', 'DiagnosticReport', 'ImmunizationRecord', 'WellnessRecord', 'HealthDocumentRecord')),
    fhir_bundle         JSONB NOT NULL,
    care_context_id     VARCHAR(100),
    is_linked_to_phr    BOOLEAN NOT NULL DEFAULT false,
    linked_at           TIMESTAMPTZ,
    consent_id          VARCHAR(100),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_abdm_records_patient ON docslot.abdm_health_records(patient_id, created_at DESC);
CREATE INDEX idx_abdm_records_abha ON docslot.abdm_health_records(abha_number);

-- ============================================================================
-- TABLE D26: ABDM_CONSENTS
-- ============================================================================
CREATE TABLE docslot.abdm_consents (
    consent_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    patient_id          UUID NOT NULL REFERENCES docslot.patients(patient_id),
    requesting_tenant_id UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    abdm_consent_request_id VARCHAR(100) NOT NULL,
    abdm_consent_artifact_id VARCHAR(100),
    purpose             VARCHAR(50) NOT NULL,
    health_info_types   VARCHAR(50)[],
    date_range_from     DATE,
    date_range_to       DATE,
    expires_at          TIMESTAMPTZ NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'requested'
        CHECK (status IN ('requested', 'granted', 'denied', 'revoked', 'expired')),
    granted_at          TIMESTAMPTZ,
    revoked_at          TIMESTAMPTZ,
    metadata            JSONB DEFAULT '{}'
);

CREATE INDEX idx_consents_patient ON docslot.abdm_consents(patient_id, status);
CREATE INDEX idx_consents_active ON docslot.abdm_consents(expires_at) WHERE status = 'granted';

-- ============================================================================
-- DOCSLOT-SPECIFIC PERMISSIONS (registered with platform.permissions)
-- ============================================================================
DO $$
DECLARE
    docslot_product_id UUID;
BEGIN
    SELECT product_id INTO docslot_product_id FROM platform.products WHERE product_key = 'docslot';

    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    -- Doctors
    ('docslot.doctor.create', docslot_product_id, 'doctor', 'create', 'tenant', 'Add doctors', false),
    ('docslot.doctor.read', docslot_product_id, 'doctor', 'read', 'tenant', 'View doctors', false),
    ('docslot.doctor.update', docslot_product_id, 'doctor', 'update', 'tenant', 'Edit doctors', false),
    ('docslot.doctor.delete', docslot_product_id, 'doctor', 'delete', 'tenant', 'Delete doctors', true),
    ('docslot.doctor.update_self', docslot_product_id, 'doctor', 'update', 'self', 'Edit own profile', false),

    -- Schedules
    ('docslot.schedule.update', docslot_product_id, 'schedule', 'update', 'tenant', 'Set any doctor schedule', false),
    ('docslot.schedule.update_self', docslot_product_id, 'schedule', 'update', 'self', 'Set own schedule', false),

    -- Slots
    ('docslot.slot.read', docslot_product_id, 'slot', 'read', 'tenant', 'View slots', false),
    ('docslot.slot.block', docslot_product_id, 'slot', 'update', 'tenant', 'Block/unblock slots', false),

    -- Bookings
    ('docslot.booking.create', docslot_product_id, 'booking', 'create', 'tenant', 'Create bookings', false),
    ('docslot.booking.read', docslot_product_id, 'booking', 'read', 'tenant', 'View all bookings', false),
    ('docslot.booking.read_self', docslot_product_id, 'booking', 'read', 'self', 'View own bookings (doctor)', false),
    ('docslot.booking.approve', docslot_product_id, 'booking', 'approve', 'tenant', 'Approve pending bookings', false),
    ('docslot.booking.cancel', docslot_product_id, 'booking', 'update', 'tenant', 'Cancel bookings', false),
    ('docslot.booking.reschedule', docslot_product_id, 'booking', 'update', 'tenant', 'Reschedule bookings', false),
    ('docslot.booking.complete', docslot_product_id, 'booking', 'update', 'tenant', 'Mark complete', false),

    -- Patients
    ('docslot.patient.read', docslot_product_id, 'patient', 'read', 'tenant', 'View patients', true),
    ('docslot.patient.update', docslot_product_id, 'patient', 'update', 'tenant', 'Update patient records', true),

    -- Prescriptions (clinical PHI — slice 03b)
    ('docslot.prescription.create', docslot_product_id, 'prescription', 'create', 'tenant', 'Issue prescriptions', true),
    ('docslot.prescription.read', docslot_product_id, 'prescription', 'read', 'tenant', 'Read prescriptions (PHI)', true),

    -- Pathology
    ('docslot.test.manage', docslot_product_id, 'test', 'update', 'tenant', 'Manage test catalog', false),
    ('docslot.report.upload', docslot_product_id, 'report', 'create', 'tenant', 'Upload lab reports', false),
    ('docslot.report.read', docslot_product_id, 'report', 'read', 'tenant', 'Read lab reports (PHI)', true),
    ('docslot.report.deliver', docslot_product_id, 'report', 'update', 'tenant', 'Deliver reports', false),

    -- Medical history (clinical PHI — slice 03b)
    ('docslot.medical_history.read', docslot_product_id, 'medical_history', 'read', 'tenant', 'Read patient medical history (PHI)', true),

    -- Procedures
    ('docslot.procedure.manage', docslot_product_id, 'procedure', 'update', 'tenant', 'Manage procedure catalog', false),

    -- ABDM
    ('docslot.abdm.records.read', docslot_product_id, 'abdm_records', 'read', 'tenant', 'Access ABDM records', true),
    ('docslot.abdm.records.create', docslot_product_id, 'abdm_records', 'create', 'tenant', 'Push records to ABDM', false),
    ('docslot.abdm.consents.manage', docslot_product_id, 'abdm_consents', 'update', 'tenant', 'Manage ABDM consents', true),

    -- Analytics
    ('docslot.analytics.read', docslot_product_id, 'analytics', 'read', 'tenant', 'View analytics', false);

    -- Assign DocSlot permissions to default tenant roles
    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_owner'
      AND p.product_id = docslot_product_id
      AND p.scope IN ('tenant', 'self');

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_admin'
      AND p.product_id = docslot_product_id
      AND p.scope IN ('tenant', 'self')
      AND p.is_dangerous = false;

    INSERT INTO platform.role_permissions (role_id, permission_id)
    SELECT r.role_id, p.permission_id
    FROM platform.roles r
    CROSS JOIN platform.permissions p
    WHERE r.role_key = 'tenant_staff'
      AND p.product_id = docslot_product_id
      AND p.permission_key IN (
        'docslot.booking.create', 'docslot.booking.read', 'docslot.booking.approve',
        'docslot.booking.cancel', 'docslot.booking.reschedule', 'docslot.booking.complete',
        'docslot.patient.read', 'docslot.patient.update',
        'docslot.report.upload', 'docslot.report.deliver',
        'docslot.slot.read', 'docslot.doctor.read'
      );
END $$;

-- Create DocSlot-specific roles
INSERT INTO platform.roles (role_key, name, description, product_id, scope, is_system)
SELECT 'doctor', 'Doctor', 'Healthcare provider — own data only', product_id, 'tenant', true
FROM platform.products WHERE product_key = 'docslot';

INSERT INTO platform.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
FROM platform.roles r
CROSS JOIN platform.permissions p
WHERE r.role_key = 'doctor'
  AND p.permission_key IN (
    'docslot.doctor.read_self', 'docslot.doctor.update_self',
    'docslot.schedule.update_self',
    'docslot.booking.read_self', 'docslot.booking.complete',
    'docslot.patient.read'
  );

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Doctor average ratings
CREATE MATERIALIZED VIEW docslot.v_doctor_ratings AS
SELECT
    doctor_id,
    AVG(rating)::DECIMAL(3,2) AS avg_rating,
    COUNT(*) AS review_count,
    COUNT(*) FILTER (WHERE rating = 5) AS five_star_count
FROM docslot.reviews
WHERE is_published = true
GROUP BY doctor_id;

CREATE UNIQUE INDEX idx_doctor_ratings ON docslot.v_doctor_ratings(doctor_id);

-- Today's bookings overview per tenant
CREATE OR REPLACE VIEW docslot.v_tenant_today_overview AS
SELECT
    t.tenant_id,
    COUNT(*) FILTER (WHERE b.booked_at::DATE = CURRENT_DATE) AS bookings_today,
    COUNT(*) FILTER (WHERE b.status = 'pending') AS pending_approvals,
    COUNT(*) FILTER (WHERE b.completed_at::DATE = CURRENT_DATE) AS completed_today,
    COUNT(*) FILTER (WHERE b.no_show_at::DATE = CURRENT_DATE) AS no_shows_today
FROM platform.tenants t
LEFT JOIN docslot.bookings b ON b.tenant_id = t.tenant_id
WHERE t.deleted_at IS NULL AND t.status = 'active'
GROUP BY t.tenant_id;

-- ============================================================================
-- AUTO-NUMBER GENERATION
-- ============================================================================

-- Auto-generate booking numbers like BKG-2026-04-00001
CREATE SEQUENCE docslot.booking_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_booking_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.booking_number IS NULL THEN
        NEW.booking_number := 'BKG-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                              LPAD(nextval('docslot.booking_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_booking_number BEFORE INSERT ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_booking_number();

-- Similar for prescription and report numbers
CREATE SEQUENCE docslot.prescription_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_prescription_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.prescription_number IS NULL THEN
        NEW.prescription_number := 'PRX-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                                   LPAD(nextval('docslot.prescription_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_prescription_number BEFORE INSERT ON docslot.prescriptions
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_prescription_number();

CREATE SEQUENCE docslot.report_number_seq;

CREATE OR REPLACE FUNCTION docslot.generate_report_number()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.report_number IS NULL THEN
        NEW.report_number := 'RPT-' || TO_CHAR(NOW(), 'YYYY-MM') || '-' ||
                             LPAD(nextval('docslot.report_number_seq')::TEXT, 5, '0');
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_report_number BEFORE INSERT ON docslot.lab_reports
    FOR EACH ROW EXECUTE FUNCTION docslot.generate_report_number();

-- ============================================================================
-- BOOKING STATUS HISTORY AUTO-LOGGING
-- ============================================================================
CREATE OR REPLACE FUNCTION docslot.log_booking_status_change()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.status IS DISTINCT FROM NEW.status THEN
        INSERT INTO docslot.booking_status_history (booking_id, from_status, to_status, changed_by_user_id)
        VALUES (NEW.booking_id, OLD.status, NEW.status, NEW.cancelled_by_user_id);
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_booking_status_log AFTER UPDATE ON docslot.bookings
    FOR EACH ROW EXECUTE FUNCTION docslot.log_booking_status_change();

-- ============================================================================
-- SLOT GENERATION (FR-BOOK-02) — materialize bookable time_slots from the
-- doctor's recurring weekly schedule, honouring schedule_overrides (blocked
-- dates skip; custom hours replace the window) and the per-block break window.
-- ============================================================================
-- SECURITY DEFINER so the nightly materializer + the staff "generate" endpoint
-- can write slots regardless of RLS; tenant_id is derived from the doctor row
-- (authoritative — never a caller-supplied value), and the calling endpoint is
-- tenant-scoped + permission-gated. Idempotent: ON CONFLICT DO NOTHING never
-- clobbers an existing (possibly already-booked) slot. Returns rows created.
CREATE OR REPLACE FUNCTION docslot.generate_time_slots(
    p_doctor_id UUID,
    p_from_date DATE,
    p_to_date   DATE
) RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_tenant       UUID;
    v_date         DATE;
    v_dow          SMALLINT;
    v_sched        RECORD;
    v_ovr          docslot.schedule_overrides%ROWTYPE;
    v_has_override BOOLEAN;
    v_win_start    TIME;
    v_win_end      TIME;
    v_slot_start   TIME;
    v_slot_end     TIME;
    v_created      INT := 0;
    v_n            INT;
BEGIN
    IF p_from_date IS NULL OR p_to_date IS NULL OR p_to_date < p_from_date THEN
        RAISE EXCEPTION 'invalid date range';
    END IF;
    IF p_to_date - p_from_date > 92 THEN
        RAISE EXCEPTION 'date range too large (max 92 days)';
    END IF;

    SELECT tenant_id INTO v_tenant
    FROM docslot.doctors
    WHERE doctor_id = p_doctor_id AND deleted_at IS NULL AND is_active = true;
    IF v_tenant IS NULL THEN
        RETURN 0;   -- unknown / inactive / deleted doctor: nothing to generate
    END IF;

    v_date := p_from_date;
    WHILE v_date <= p_to_date LOOP
        v_dow := EXTRACT(DOW FROM v_date)::SMALLINT;   -- 0=Sunday .. 6=Saturday

        SELECT * INTO v_ovr FROM docslot.schedule_overrides
            WHERE doctor_id = p_doctor_id AND override_date = v_date;
        v_has_override := FOUND;

        -- A blocked override (holiday/leave) skips the whole day.
        IF v_has_override AND v_ovr.is_blocked THEN
            v_date := v_date + 1;
            CONTINUE;
        END IF;

        FOR v_sched IN
            SELECT * FROM docslot.doctor_schedules
            WHERE doctor_id = p_doctor_id AND day_of_week = v_dow AND is_active = true
        LOOP
            -- Custom override hours (if provided) replace the schedule's window for this date.
            IF v_has_override AND v_ovr.custom_start_time IS NOT NULL THEN
                v_win_start := v_ovr.custom_start_time;
                v_win_end   := COALESCE(v_ovr.custom_end_time, v_sched.end_time);
            ELSE
                v_win_start := v_sched.start_time;
                v_win_end   := v_sched.end_time;
            END IF;

            v_slot_start := v_win_start;
            WHILE v_slot_start + make_interval(mins => v_sched.slot_duration_minutes) <= v_win_end LOOP
                v_slot_end := v_slot_start + make_interval(mins => v_sched.slot_duration_minutes);

                -- Skip any slot overlapping the break window.
                IF v_sched.break_start_time IS NULL
                   OR NOT (v_slot_start < v_sched.break_end_time AND v_slot_end > v_sched.break_start_time) THEN
                    INSERT INTO docslot.time_slots
                        (tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count)
                    VALUES
                        (v_tenant, p_doctor_id, v_date, v_slot_start, v_slot_end, 'available', 0, v_sched.max_patients_per_slot)
                    ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING;
                    GET DIAGNOSTICS v_n = ROW_COUNT;
                    v_created := v_created + v_n;
                END IF;

                v_slot_start := v_slot_end;
            END LOOP;
        END LOOP;

        v_date := v_date + 1;
    END LOOP;

    RETURN v_created;
END;
$$;
COMMENT ON FUNCTION docslot.generate_time_slots IS
    'Materialize bookable time_slots for a doctor over [from,to] from doctor_schedules, honouring schedule_overrides + breaks. Idempotent (ON CONFLICT DO NOTHING). tenant_id derived from the doctor. Returns rows created.';

-- Sweep stale live slot holds to 'expired'. SECURITY DEFINER so the maintenance worker
-- (which has no per-request tenant context) can run it under RLS on slot_holds — a plain
-- app-role UPDATE would match zero rows once app.tenant_id is unset. Returns rows swept.
CREATE OR REPLACE FUNCTION docslot.expire_stale_slot_holds()
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    UPDATE docslot.slot_holds
    SET status = 'expired'
    WHERE status = 'held' AND expires_at < NOW();
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.expire_stale_slot_holds IS
    'Mark stale held slot_holds (expires_at < now) as expired. SECURITY DEFINER so the RLS-less maintenance worker can sweep across all tenants. Returns rows swept.';

-- ----------------------------------------------------------------------------
-- requeue_stranded_outbox(p_older_than) — outbox 'processing' reaper
-- ----------------------------------------------------------------------------
-- If the drain worker dies mid-send (or a clean shutdown interrupts a send), the
-- claimed row is left in 'processing' and the claim query (status='pending') will
-- never pick it up again → silent message loss. This reaper requeues any row stuck
-- in 'processing' beyond p_older_than back to 'pending' (due now), so the next
-- drain re-attempts it. SECURITY DEFINER: the maintenance worker has no per-request
-- tenant context, so a plain app-role UPDATE would match zero rows under outbox RLS.
CREATE OR REPLACE FUNCTION docslot.requeue_stranded_outbox(p_older_than INTERVAL DEFAULT INTERVAL '5 minutes')
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    UPDATE docslot.outbox_messages
    SET status = 'pending', next_retry_at = NOW()
    WHERE status = 'processing'
      AND created_at < NOW() - p_older_than;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.requeue_stranded_outbox IS
    'Requeue outbox rows stuck in processing (worker died mid-send) back to pending. SECURITY DEFINER for the RLS-less maintenance worker. Returns rows requeued.';

-- ----------------------------------------------------------------------------
-- expire_stale_consent_otps() — behalf-booking consent OTP expiry
-- ----------------------------------------------------------------------------
-- A behalf booking is created in 'pending' with patient_consent_status='pending'
-- and an OTP sent to the patient. If the patient never approves before the OTP
-- expires, the OTP is marked 'expired', the booking's consent is flipped to
-- 'expired', the booking is cancelled, and the slot capacity it held is freed
-- (decrement current_count, re-open a 'booked' slot) so it can be rebooked.
-- SECURITY DEFINER: runs in the RLS-less maintenance worker across all tenants.
CREATE OR REPLACE FUNCTION docslot.expire_stale_consent_otps()
RETURNS INT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
DECLARE
    v_n INT;
BEGIN
    WITH expired AS (
        UPDATE docslot.booking_consent_otps o
        SET status = 'expired'
        WHERE o.status = 'pending' AND o.expires_at < NOW()
        RETURNING o.booking_id
    ),
    cancelled AS (
        UPDATE docslot.bookings b
        SET status = 'cancelled',
            patient_consent_status = 'expired',
            cancellation_reason = COALESCE(b.cancellation_reason, 'Patient consent OTP expired'),
            cancelled_at = NOW()
        FROM expired e
        WHERE b.booking_id = e.booking_id
          AND b.status IN ('pending', 'confirmed')
          AND b.patient_consent_status = 'pending'
        RETURNING b.slot_id
    )
    UPDATE docslot.time_slots s
    SET current_count = GREATEST(s.current_count - 1, 0),
        status = CASE WHEN s.status = 'booked' THEN 'available' ELSE s.status END
    FROM cancelled c
    WHERE s.slot_id = c.slot_id;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END;
$$;
COMMENT ON FUNCTION docslot.expire_stale_consent_otps IS
    'Expire behalf-booking consent OTPs past expiry: mark OTP expired, cancel the awaiting booking, free its slot capacity. SECURITY DEFINER for the RLS-less maintenance worker. Returns slots freed.';

-- ----------------------------------------------------------------------------
-- Outbox drain functions (SECURITY DEFINER) — RLS bypass for the context-less worker
-- ----------------------------------------------------------------------------
-- outbox_messages is RLS-protected (file 05), but the drain worker is cross-tenant
-- by design and runs with NO app.tenant_id set — a plain app-role query would match
-- zero rows. These definer functions let the worker claim/mark across all tenants
-- (the same pattern as expire_stale_slot_holds / requeue_stranded_outbox). Enqueue
-- and the conversation read still go through RLS with a tenant context.
CREATE OR REPLACE FUNCTION docslot.claim_due_outbox(p_batch INT)
RETURNS TABLE(outbox_id UUID, tenant_id UUID, patient_id UUID, message_intent TEXT,
              to_phone TEXT, body TEXT, correlation_id TEXT, attempt_count INT, max_attempts INT)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    RETURN QUERY
    WITH due AS (
        SELECT o.outbox_id
        FROM docslot.outbox_messages o
        WHERE o.status = 'pending'
          AND (o.next_retry_at IS NULL OR o.next_retry_at <= now())
        ORDER BY o.created_at
        FOR UPDATE SKIP LOCKED
        LIMIT p_batch
    )
    UPDATE docslot.outbox_messages o
    SET status = 'processing'
    FROM due
    WHERE o.outbox_id = due.outbox_id
    RETURNING o.outbox_id, o.tenant_id, o.patient_id, o.message_intent::text,
              o.payload->>'to', o.payload->>'text', o.correlation_id::text,
              o.attempt_count::int, o.max_attempts::int;
END;
$$;
COMMENT ON FUNCTION docslot.claim_due_outbox IS 'Atomically claim due pending outbox rows (→processing) across tenants. SECURITY DEFINER for the RLS-less drain worker.';

CREATE OR REPLACE FUNCTION docslot.mark_outbox_sent(p_outbox_id UUID, p_provider_id TEXT, p_now TIMESTAMPTZ)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    -- Mark delivered AND scrub the message body for consent OTPs so the live code does not linger in the
    -- queue after delivery (DPDP — the code is a one-time secret; the consent row keeps only a salted hash).
    UPDATE docslot.outbox_messages
    SET status = 'sent',
        sent_at = p_now,
        whatsapp_message_id = p_provider_id,
        last_error = NULL,
        payload = CASE WHEN message_intent = 'consent_otp'
                       THEN jsonb_set(payload, '{text}', '"[redacted after send]"'::jsonb)
                       ELSE payload END
    WHERE outbox_id = p_outbox_id;
END;
$$;
COMMENT ON FUNCTION docslot.mark_outbox_sent IS 'Mark an outbox row sent (scrubs the consent-OTP body post-delivery). SECURITY DEFINER for the RLS-less drain worker.';

CREATE OR REPLACE FUNCTION docslot.mark_outbox_failed(p_outbox_id UUID, p_error TEXT, p_next_retry_at TIMESTAMPTZ)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = docslot, pg_temp
AS $$
BEGIN
    -- Increment attempt; terminal 'abandoned' at max_attempts, else back to 'pending' with the backoff. On
    -- TERMINAL abandon, scrub the consent-OTP body so the one-time code doesn't linger in a dead-lettered row
    -- (auditor F4). A retry (→pending) must KEEP the real text — it is the message still being delivered.
    UPDATE docslot.outbox_messages
    SET attempt_count = attempt_count + 1,
        last_error = p_error,
        status = CASE WHEN attempt_count + 1 >= max_attempts THEN 'abandoned' ELSE 'pending' END,
        next_retry_at = CASE WHEN attempt_count + 1 >= max_attempts THEN next_retry_at ELSE p_next_retry_at END,
        payload = CASE WHEN attempt_count + 1 >= max_attempts AND message_intent = 'consent_otp'
                       THEN jsonb_set(payload, '{text}', '"[redacted after send]"'::jsonb)
                       ELSE payload END
    WHERE outbox_id = p_outbox_id;
END;
$$;
COMMENT ON FUNCTION docslot.mark_outbox_failed IS 'Record a failed outbox send (retry/backoff or abandon; scrubs consent-OTP body on terminal abandon). SECURITY DEFINER for the RLS-less drain worker.';

-- ============================================================================
-- END OF DOCSLOT SCHEMA
-- ============================================================================
-- Tables: 26 (D1-D26)
-- Total platform + docslot tables: 52
-- Next: 04_ruralreach.sql, 05_safeher.sql, 06_genericfirst.sql (optional)
-- ============================================================================
