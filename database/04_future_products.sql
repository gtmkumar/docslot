-- ============================================================================
-- DocSlot Platform Database — Part 4: Future Product Schemas (Optional)
-- ============================================================================
-- These schemas are for the three adjacent products documented in
-- FUTURE_PRODUCTS.md. Run only if/when these products are being built.
-- Each schema is independent — you can deploy them separately.
-- ============================================================================

-- ============================================================================
-- 04. RURALREACH — Mobile diagnostic logistics
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS ruralreach;
COMMENT ON SCHEMA ruralreach IS 'Mobile diagnostic vans + home sample collection logistics';

-- Vehicle fleet
CREATE TABLE ruralreach.vehicles (
    vehicle_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    vehicle_number      VARCHAR(20) NOT NULL UNIQUE,
    vehicle_type        VARCHAR(30) NOT NULL CHECK (vehicle_type IN ('van', 'two_wheeler', 'car', 'truck')),
    capacity_samples    INT NOT NULL DEFAULT 50,
    has_refrigeration   BOOLEAN NOT NULL DEFAULT false,
    min_temp_celsius    DECIMAL(4,1),
    max_temp_celsius    DECIMAL(4,1),
    current_status      VARCHAR(20) NOT NULL DEFAULT 'available' CHECK (current_status IN ('available', 'on_route', 'maintenance', 'off_duty')),
    current_lat         DECIMAL(10,7),
    current_lng         DECIMAL(10,7),
    last_gps_update     TIMESTAMPTZ,
    fuel_type           VARCHAR(20),
    registration_expires DATE,
    insurance_expires   DATE,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_vehicles_tenant ON ruralreach.vehicles(tenant_id) WHERE is_active = true;
CREATE INDEX idx_vehicles_status ON ruralreach.vehicles(current_status) WHERE is_active = true;
CREATE TRIGGER trg_vehicles_updated_at BEFORE UPDATE ON ruralreach.vehicles
    FOR EACH ROW EXECUTE FUNCTION platform.set_updated_at();

-- Field technicians
CREATE TABLE ruralreach.technicians (
    technician_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    user_id             UUID REFERENCES platform.users(user_id),
    full_name           VARCHAR(200) NOT NULL,
    gender              VARCHAR(10),
    phone               VARCHAR(15) NOT NULL,
    employee_id         VARCHAR(50),
    certifications      JSONB DEFAULT '[]',                     -- [{name, issued_by, expires_at}]
    specialized_tests   VARCHAR(50)[],                          -- Can perform: blood, ECG, X-ray
    current_lat         DECIMAL(10,7),
    current_lng         DECIMAL(10,7),
    is_available        BOOLEAN NOT NULL DEFAULT true,
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_technicians_tenant ON ruralreach.technicians(tenant_id) WHERE is_active = true;
CREATE INDEX idx_technicians_available ON ruralreach.technicians(tenant_id) WHERE is_available = true AND is_active = true;

-- Service zones
CREATE TABLE ruralreach.service_zones (
    zone_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    zone_name           VARCHAR(100) NOT NULL,
    pin_codes           VARCHAR(10)[],
    boundary_polygon    JSONB,                                   -- GeoJSON polygon
    service_fee         DECIMAL(10,2),
    estimated_arrival_minutes INT,
    is_active           BOOLEAN NOT NULL DEFAULT true
);

-- Collection requests (links to DocSlot bookings)
CREATE TABLE ruralreach.collection_requests (
    request_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),

    -- Link to source booking (cross-product reference)
    source_booking_id   UUID,                                    -- docslot.bookings.booking_id
    patient_id          UUID,                                    -- docslot.patients.patient_id
    patient_phone       VARCHAR(15) NOT NULL,
    patient_name        VARCHAR(200) NOT NULL,

    -- Collection details
    tests_requested     JSONB NOT NULL,                          -- Array of test_ids from docslot.test_catalog
    collection_address  TEXT NOT NULL,
    pickup_lat          DECIMAL(10,7),
    pickup_lng          DECIMAL(10,7),
    pin_code            VARCHAR(10) NOT NULL,
    zone_id             UUID REFERENCES ruralreach.service_zones(zone_id),

    -- Scheduling
    requested_window_start TIMESTAMPTZ NOT NULL,
    requested_window_end TIMESTAMPTZ NOT NULL,
    assigned_vehicle_id UUID REFERENCES ruralreach.vehicles(vehicle_id),
    assigned_technician_id UUID REFERENCES ruralreach.technicians(technician_id),
    estimated_arrival_at TIMESTAMPTZ,

    -- Status tracking
    status              VARCHAR(30) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'assigned', 'en_route', 'arrived', 'collected', 'in_transit_to_lab', 'delivered_to_lab', 'cancelled', 'failed')),

    -- Actuals
    technician_arrived_at TIMESTAMPTZ,
    collected_at        TIMESTAMPTZ,
    delivered_to_lab_at TIMESTAMPTZ,
    target_lab_tenant_id UUID REFERENCES platform.tenants(tenant_id),  -- Which DocSlot lab receives the sample

    -- Pricing
    base_fee            DECIMAL(10,2),
    zone_fee            DECIMAL(10,2),
    total_amount        DECIMAL(10,2),

    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_requests_status ON ruralreach.collection_requests(status, requested_window_start);
CREATE INDEX idx_requests_technician ON ruralreach.collection_requests(assigned_technician_id) WHERE status IN ('assigned', 'en_route', 'arrived');
CREATE INDEX idx_requests_zone ON ruralreach.collection_requests(zone_id, requested_window_start);
CREATE INDEX idx_requests_source_booking ON ruralreach.collection_requests(source_booking_id) WHERE source_booking_id IS NOT NULL;

-- Sample chain of custody
CREATE TABLE ruralreach.sample_chain_of_custody (
    custody_id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id          UUID NOT NULL REFERENCES ruralreach.collection_requests(request_id) ON DELETE CASCADE,
    sample_barcode      VARCHAR(50) NOT NULL UNIQUE,
    sample_type         VARCHAR(50),
    collected_at        TIMESTAMPTZ NOT NULL,
    collected_by_technician_id UUID REFERENCES ruralreach.technicians(technician_id),
    handover_to_vehicle_id UUID REFERENCES ruralreach.vehicles(vehicle_id),
    handover_at         TIMESTAMPTZ,
    delivered_to_lab_at TIMESTAMPTZ,
    received_by_user_id UUID REFERENCES platform.users(user_id),
    rejected            BOOLEAN NOT NULL DEFAULT false,
    rejection_reason    TEXT
);

CREATE INDEX idx_custody_request ON ruralreach.sample_chain_of_custody(request_id);
CREATE INDEX idx_custody_barcode ON ruralreach.sample_chain_of_custody(sample_barcode);

-- Cold chain temperature logs
CREATE TABLE ruralreach.cold_chain_logs (
    log_id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    vehicle_id          UUID NOT NULL REFERENCES ruralreach.vehicles(vehicle_id),
    request_id          UUID REFERENCES ruralreach.collection_requests(request_id),
    temperature_celsius DECIMAL(4,1) NOT NULL,
    humidity_percent    SMALLINT,
    is_within_range     BOOLEAN NOT NULL,
    recorded_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_cold_chain_vehicle_time ON ruralreach.cold_chain_logs(vehicle_id, recorded_at DESC);
CREATE INDEX idx_cold_chain_violations ON ruralreach.cold_chain_logs(vehicle_id, recorded_at)
    WHERE is_within_range = false;

-- Routes (optimized daily routes for vehicles)
CREATE TABLE ruralreach.routes (
    route_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    vehicle_id          UUID NOT NULL REFERENCES ruralreach.vehicles(vehicle_id),
    technician_id       UUID NOT NULL REFERENCES ruralreach.technicians(technician_id),
    route_date          DATE NOT NULL,
    planned_start_at    TIMESTAMPTZ,
    actual_start_at     TIMESTAMPTZ,
    actual_end_at       TIMESTAMPTZ,
    total_stops         INT NOT NULL DEFAULT 0,
    completed_stops     INT NOT NULL DEFAULT 0,
    total_distance_km   DECIMAL(8,2),
    optimized_path      JSONB,                                   -- Array of waypoints
    status              VARCHAR(20) NOT NULL DEFAULT 'planned'
        CHECK (status IN ('planned', 'in_progress', 'completed', 'cancelled'))
);

CREATE TABLE ruralreach.route_stops (
    stop_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    route_id            UUID NOT NULL REFERENCES ruralreach.routes(route_id) ON DELETE CASCADE,
    request_id          UUID NOT NULL REFERENCES ruralreach.collection_requests(request_id),
    sequence_number     SMALLINT NOT NULL,
    planned_arrival_at  TIMESTAMPTZ,
    actual_arrival_at   TIMESTAMPTZ,
    actual_departure_at TIMESTAMPTZ,
    UNIQUE(route_id, sequence_number)
);

-- ============================================================================
-- 05. SAFEHER — Women's healthcare access
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS safeher;
COMMENT ON SCHEMA safeher IS 'Women-focused healthcare access — same-gender services, female-only slots, chaperone marketplace';

-- Female staff registry (extends docslot.doctors with gender-specific data)
CREATE TABLE safeher.female_staff (
    staff_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id) ON DELETE CASCADE,
    doctor_id           UUID REFERENCES docslot.doctors(doctor_id),  -- If they're also a doctor in DocSlot
    technician_id       UUID REFERENCES ruralreach.technicians(technician_id),  -- Or technician in RuralReach
    full_name           VARCHAR(200) NOT NULL,
    languages_spoken    VARCHAR(10)[] NOT NULL DEFAULT ARRAY['hi','en'],
    specializations     VARCHAR(100)[],
    background_verified BOOLEAN NOT NULL DEFAULT false,
    background_verified_at TIMESTAMPTZ,
    photo_url           TEXT,
    bio                 TEXT,
    is_available_for_chaperone BOOLEAN NOT NULL DEFAULT false,
    chaperone_hourly_rate DECIMAL(10,2),
    is_active           BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_female_staff_tenant ON safeher.female_staff(tenant_id) WHERE is_active = true;
CREATE INDEX idx_female_staff_chaperone ON safeher.female_staff(tenant_id) WHERE is_available_for_chaperone = true;

-- Female-only slots (overlay on docslot.time_slots)
CREATE TABLE safeher.female_only_slots (
    slot_designation_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    slot_id             UUID NOT NULL UNIQUE,                    -- References docslot.time_slots
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    designation_reason  VARCHAR(100),
    requires_female_doctor BOOLEAN NOT NULL DEFAULT true,
    requires_female_technician BOOLEAN NOT NULL DEFAULT true,
    designated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    designated_by_user_id UUID REFERENCES platform.users(user_id)
);

-- Chaperone bookings
CREATE TABLE safeher.chaperone_bookings (
    chaperone_booking_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    related_booking_id  UUID,                                    -- docslot.bookings.booking_id
    patient_phone       VARCHAR(15) NOT NULL,
    patient_name        VARCHAR(200),
    chaperone_staff_id  UUID NOT NULL REFERENCES safeher.female_staff(staff_id),
    pickup_address      TEXT NOT NULL,
    destination_address TEXT NOT NULL,
    scheduled_pickup_at TIMESTAMPTZ NOT NULL,
    estimated_duration_hours DECIMAL(4,2),
    actual_pickup_at    TIMESTAMPTZ,
    actual_return_at    TIMESTAMPTZ,
    hourly_rate         DECIMAL(10,2),
    total_amount        DECIMAL(10,2),
    status              VARCHAR(20) NOT NULL DEFAULT 'requested'
        CHECK (status IN ('requested', 'confirmed', 'in_progress', 'completed', 'cancelled', 'no_show')),
    is_anonymous_booking BOOLEAN NOT NULL DEFAULT false,         -- For sensitive cases
    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_chaperone_status ON safeher.chaperone_bookings(status, scheduled_pickup_at);
CREATE INDEX idx_chaperone_staff ON safeher.chaperone_bookings(chaperone_staff_id, scheduled_pickup_at DESC);

-- Privacy preferences (per patient)
CREATE TABLE safeher.patient_privacy_preferences (
    patient_id          UUID PRIMARY KEY,                        -- References docslot.patients
    requires_female_provider BOOLEAN NOT NULL DEFAULT false,
    requires_female_only_facility BOOLEAN NOT NULL DEFAULT false,
    requires_chaperone  BOOLEAN NOT NULL DEFAULT false,
    hide_from_family_phone BOOLEAN NOT NULL DEFAULT false,       -- For sensitive bookings (the phone might be shared)
    secondary_phone     VARCHAR(15),                              -- Discreet contact number
    secondary_phone_use VARCHAR(50),                              -- 'reminders_only', 'confirmations_only'
    preferred_pickup_landmarks TEXT,                              -- Discreet pickup locations
    notes               TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Anonymous bookings (for sensitive procedures — partner shouldn't know)
CREATE TABLE safeher.anonymous_bookings (
    anon_booking_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    anonymous_code      VARCHAR(20) NOT NULL UNIQUE,             -- Patient receives only this
    related_booking_id  UUID,                                    -- docslot.bookings.booking_id, encrypted FK
    secondary_phone     VARCHAR(15) NOT NULL,
    contact_window_start TIME,                                    -- Only call during these hours
    contact_window_end  TIME,
    contact_method      VARCHAR(20) DEFAULT 'whatsapp_only',
    expires_at          TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Women's-only zones (clinics, waiting areas)
CREATE TABLE safeher.female_only_facilities (
    facility_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL UNIQUE REFERENCES platform.tenants(tenant_id),
    has_separate_entrance BOOLEAN NOT NULL DEFAULT false,
    has_female_only_waiting_area BOOLEAN NOT NULL DEFAULT false,
    has_female_only_restrooms BOOLEAN NOT NULL DEFAULT false,
    has_curtained_examination BOOLEAN NOT NULL DEFAULT true,
    all_female_staff    BOOLEAN NOT NULL DEFAULT false,
    certifications      JSONB DEFAULT '[]',
    photo_urls          TEXT[],
    verified_at         TIMESTAMPTZ
);

-- Reviews specifically about female-friendliness
CREATE TABLE safeher.female_friendly_reviews (
    review_id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    related_booking_id  UUID,                                    -- docslot.bookings
    patient_id          UUID,                                    -- docslot.patients
    privacy_rating      SMALLINT CHECK (privacy_rating BETWEEN 1 AND 5),
    staff_sensitivity_rating SMALLINT CHECK (staff_sensitivity_rating BETWEEN 1 AND 5),
    facility_rating     SMALLINT CHECK (facility_rating BETWEEN 1 AND 5),
    chaperone_rating    SMALLINT CHECK (chaperone_rating BETWEEN 1 AND 5),
    comment             TEXT,
    is_published        BOOLEAN NOT NULL DEFAULT true,
    is_anonymous        BOOLEAN NOT NULL DEFAULT true,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_female_reviews_tenant ON safeher.female_friendly_reviews(tenant_id) WHERE is_published = true;

-- Cultural preferences (regional context)
CREATE TABLE safeher.cultural_preferences (
    preference_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id           UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    region              VARCHAR(50),
    supported_languages VARCHAR(10)[],
    respects_purdah     BOOLEAN NOT NULL DEFAULT false,
    veil_friendly_examination BOOLEAN NOT NULL DEFAULT false,
    family_decision_protocol VARCHAR(50),                        -- 'patient_only', 'spouse_required', 'family_council'
    metadata            JSONB DEFAULT '{}'
);

-- ============================================================================
-- 06. GENERICFIRST — Prescription decision support
-- ============================================================================
CREATE SCHEMA IF NOT EXISTS genericfirst;
COMMENT ON SCHEMA genericfirst IS 'Generic drug intelligence — suggests cheaper equivalents at prescription time';

-- Master drug database
CREATE TABLE genericfirst.drugs (
    drug_id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    active_ingredient   VARCHAR(200) NOT NULL,
    brand_name          VARCHAR(200),
    is_generic          BOOLEAN NOT NULL DEFAULT false,
    manufacturer        VARCHAR(200),
    dosage_form         VARCHAR(50),                              -- 'tablet', 'syrup', 'injection', 'cream'
    strength            VARCHAR(50),                              -- '500mg', '5%'
    pack_size           VARCHAR(50),                              -- '10 tablets', '100ml'
    mrp                 DECIMAL(10,2),
    average_market_price DECIMAL(10,2),
    jan_aushadhi_price  DECIMAL(10,2),                            -- PMBJP government generic price
    cdsco_approved      BOOLEAN NOT NULL DEFAULT false,
    cdsco_approval_number VARCHAR(50),
    therapeutic_class   VARCHAR(100),
    requires_prescription BOOLEAN NOT NULL DEFAULT true,
    is_scheduled_drug   BOOLEAN NOT NULL DEFAULT false,           -- Schedule H/X drugs
    schedule_classification VARCHAR(10),
    contraindications   TEXT,
    side_effects        TEXT,
    metadata            JSONB DEFAULT '{}',
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_drugs_ingredient ON genericfirst.drugs(active_ingredient);
CREATE INDEX idx_drugs_brand ON genericfirst.drugs(brand_name);
CREATE INDEX idx_drugs_search ON genericfirst.drugs USING gin(brand_name gin_trgm_ops);

-- Equivalence mappings (branded → generic alternatives)
CREATE TABLE genericfirst.drug_equivalence (
    equivalence_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branded_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    generic_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    equivalence_rating  VARCHAR(10) NOT NULL CHECK (equivalence_rating IN ('AB', 'AA', 'BX', 'BA', 'BB', 'BC')),  -- FDA-style ratings
    bioequivalence_study_url TEXT,
    notes               TEXT,
    verified_by_pharma_expert BOOLEAN NOT NULL DEFAULT false,
    verified_at         TIMESTAMPTZ,
    UNIQUE(branded_drug_id, generic_drug_id)
);

CREATE INDEX idx_equivalence_branded ON genericfirst.drug_equivalence(branded_drug_id);

-- Bioequivalence studies (research references)
CREATE TABLE genericfirst.bioequivalence_studies (
    study_id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    branded_drug_id     UUID REFERENCES genericfirst.drugs(drug_id),
    generic_drug_id     UUID REFERENCES genericfirst.drugs(drug_id),
    study_title         TEXT NOT NULL,
    journal_name        VARCHAR(200),
    publication_date    DATE,
    doi                 VARCHAR(100),
    pdf_url             TEXT,
    sample_size         INT,
    auc_ratio           DECIMAL(5,3),                             -- Area Under Curve ratio
    cmax_ratio          DECIMAL(5,3),                             -- Max concentration ratio
    confidence_interval_90 VARCHAR(50),
    conclusion          TEXT,
    metadata            JSONB DEFAULT '{}'
);

-- Drug interactions database
CREATE TABLE genericfirst.drug_interactions (
    interaction_id      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    drug_id_1           UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    drug_id_2           UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    severity            VARCHAR(20) NOT NULL CHECK (severity IN ('minor', 'moderate', 'major', 'contraindicated')),
    description         TEXT NOT NULL,
    clinical_effect     TEXT,
    management_advice   TEXT,
    references_doi      VARCHAR(100),
    CHECK (drug_id_1 < drug_id_2)                                 -- Avoid duplicate pairs
);

CREATE INDEX idx_interactions_drug1 ON genericfirst.drug_interactions(drug_id_1);
CREATE INDEX idx_interactions_drug2 ON genericfirst.drug_interactions(drug_id_2);

-- Prescription suggestions log (track what was suggested vs. what was prescribed)
CREATE TABLE genericfirst.prescription_suggestions (
    suggestion_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prescription_id     UUID,                                    -- docslot.prescriptions.prescription_id
    doctor_id           UUID,                                    -- docslot.doctors.doctor_id
    branded_drug_id     UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    suggested_generic_drug_id UUID REFERENCES genericfirst.drugs(drug_id),
    suggested_jan_aushadhi BOOLEAN NOT NULL DEFAULT false,
    estimated_savings_inr DECIMAL(10,2),
    doctor_accepted     BOOLEAN,
    doctor_decline_reason VARCHAR(200),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_suggestions_doctor ON genericfirst.prescription_suggestions(doctor_id, created_at DESC);
CREATE INDEX idx_suggestions_accepted ON genericfirst.prescription_suggestions(doctor_accepted, created_at DESC);

-- Pharmacy availability (which pharmacies stock which generics)
CREATE TABLE genericfirst.pharmacy_availability (
    availability_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    pharmacy_tenant_id  UUID NOT NULL REFERENCES platform.tenants(tenant_id),
    drug_id             UUID NOT NULL REFERENCES genericfirst.drugs(drug_id),
    in_stock            BOOLEAN NOT NULL DEFAULT false,
    quantity_available  INT,
    selling_price       DECIMAL(10,2),
    last_updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(pharmacy_tenant_id, drug_id)
);

CREATE INDEX idx_pharmacy_stock ON genericfirst.pharmacy_availability(drug_id, in_stock, selling_price);

-- ============================================================================
-- FUTURE PRODUCT PERMISSIONS
-- ============================================================================
DO $$
DECLARE
    rural_id UUID;
    safe_id UUID;
    generic_id UUID;
BEGIN
    SELECT product_id INTO rural_id FROM platform.products WHERE product_key = 'ruralreach';
    SELECT product_id INTO safe_id FROM platform.products WHERE product_key = 'safeher';
    SELECT product_id INTO generic_id FROM platform.products WHERE product_key = 'genericfirst';

    -- RuralReach permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('ruralreach.vehicle.manage', rural_id, 'vehicle', 'update', 'tenant', 'Manage vehicle fleet', false),
    ('ruralreach.technician.manage', rural_id, 'technician', 'update', 'tenant', 'Manage technicians', false),
    ('ruralreach.request.create', rural_id, 'request', 'create', 'tenant', 'Create collection requests', false),
    ('ruralreach.request.dispatch', rural_id, 'request', 'update', 'tenant', 'Dispatch vehicles', false),
    ('ruralreach.route.optimize', rural_id, 'route', 'update', 'tenant', 'Optimize routes', false),
    ('ruralreach.cold_chain.read', rural_id, 'cold_chain', 'read', 'tenant', 'View temperature logs', false);

    -- SafeHer permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('safeher.female_staff.manage', safe_id, 'female_staff', 'update', 'tenant', 'Manage female staff registry', false),
    ('safeher.chaperone.book', safe_id, 'chaperone', 'create', 'tenant', 'Book chaperone services', false),
    ('safeher.facility.certify', safe_id, 'facility', 'update', 'platform', 'Certify female-only facilities', true),
    ('safeher.anonymous_bookings.read', safe_id, 'anonymous_bookings', 'read', 'tenant', 'View anonymous bookings (audit only)', true);

    -- GenericFirst permissions
    INSERT INTO platform.permissions (permission_key, product_id, resource, action, scope, description, is_dangerous) VALUES
    ('genericfirst.drugs.read', generic_id, 'drugs', 'read', 'tenant', 'Search drug database', false),
    ('genericfirst.drugs.manage', generic_id, 'drugs', 'update', 'platform', 'Manage master drug database', true),
    ('genericfirst.suggestions.read', generic_id, 'suggestions', 'read', 'tenant', 'View prescription suggestions', false),
    ('genericfirst.interactions.read', generic_id, 'interactions', 'read', 'tenant', 'View drug interactions', false);
END $$;

-- ============================================================================
-- CROSS-PRODUCT VIEWS (queries that span multiple products)
-- ============================================================================

-- Unified patient view across DocSlot + RuralReach
CREATE OR REPLACE VIEW platform.v_patient_full_view AS
SELECT
    p.patient_id,
    p.full_name,
    p.phone_number,
    p.preferred_language,
    -- DocSlot data
    (SELECT COUNT(*) FROM docslot.bookings WHERE patient_id = p.patient_id) AS total_docslot_bookings,
    -- RuralReach data
    (SELECT COUNT(*) FROM ruralreach.collection_requests WHERE patient_phone = p.phone_number) AS total_home_collections,
    -- SafeHer flags
    CASE WHEN EXISTS (
        SELECT 1 FROM safeher.patient_privacy_preferences WHERE patient_id = p.patient_id
    ) THEN true ELSE false END AS has_privacy_preferences
FROM docslot.patients p
WHERE p.deleted_at IS NULL;

-- ============================================================================
-- END OF FUTURE PRODUCT SCHEMAS
-- ============================================================================
-- Tables created:
--   RuralReach:    8 (vehicles, technicians, service_zones, collection_requests,
--                     sample_chain_of_custody, cold_chain_logs, routes, route_stops)
--   SafeHer:       8 (female_staff, female_only_slots, chaperone_bookings,
--                     patient_privacy_preferences, anonymous_bookings,
--                     female_only_facilities, female_friendly_reviews, cultural_preferences)
--   GenericFirst:  7 (drugs, drug_equivalence, bioequivalence_studies, drug_interactions,
--                     prescription_suggestions, pharmacy_availability + extensible)
--
-- TOTAL PLATFORM TABLES: 26 (platform + platform_api)
-- TOTAL DOCSLOT TABLES:  26
-- TOTAL FUTURE TABLES:   23
-- GRAND TOTAL:           97 tables across 7 schemas (after 01-06 all loaded)
-- ============================================================================
