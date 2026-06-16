# DocSlot Data Dictionary — Complete A-Z Reference

> **Authoritative column-level reference** for all 113 tables across 8 schemas (1,627 columns). Auto-derived from canonical `database/*.sql`. When SQL and this dictionary disagree, SQL wins.

## How to use

Ctrl+F a table name (`platform.users`). Notation: `PK`, `FK→table`, `NOT NULL`, `UNIQUE`, `default`.

---

## Schema Index

| Schema | Count | Purpose | Source files |
|---|---|---|---|
| `platform` | 36 tables | Cross-product platform: identity, RBAC (menus + overrides), audit, encryption, billing | 01 + 05 + 08 |
| `platform_api` | 8 tables | Platform-as-a-Service: OAuth 2.0, scoped JWTs, webhooks | 02 |
| `docslot` | 27 tables | DocSlot product: booking, prescriptions, ABDM, WhatsApp state + chat identity | 03 + 09 |
| `ai` | 10 tables | AI services: LangGraph, embeddings, predictions, OCR | 06 |
| `commission` | 10 tables | Broker referral economy: brokers, attribution, rules, payouts (TDS/GST), disputes. PCPNDT CHECK-enforced | 07 |
| `ruralreach` | 8 tables | RuralReach (future): mobile diagnostics logistics | 04 |
| `safeher` | 8 tables | SafeHer (future): women-only healthcare | 04 |
| `genericfirst` | 6 tables | GenericFirst (future): generic drug intelligence | 04 |

**Total: 113 tables, 1,627 columns across 8 schemas.**

---

## Table Index (A-Z)

### platform.* (36 tables)

- `platform.access_policies` — 11 columns
- `platform.action_types` — 8 columns
- `platform.alerts` — 12 columns
- `platform.anomaly_events` — 15 columns
- `platform.audit_anchors` — 8 columns
- `platform.audit_chain` — 6 columns
- `platform.audit_log` — 22 columns
- `platform.breach_log` — 20 columns
- `platform.consent_event_log` — 17 columns
- `platform.data_deletion_requests` — 17 columns
- `platform.data_export_requests` — 24 columns
- `platform.deletion_certificates` — 18 columns
- `platform.encrypted_fields_registry` — 12 columns
- `platform.encryption_keys` — 16 columns
- `platform.ip_allowlist` — 9 columns
- `platform.key_usage_log` — 12 columns
- `platform.login_attempts` — 7 columns
- `platform.menu_permissions` — 5 columns
- `platform.navigation_menus` — 18 columns
- `platform.notifications` — 14 columns
- `platform.password_reset_tokens` — 7 columns
- `platform.permissions` — 10 columns
- `platform.platform_settings` — 8 columns
- `platform.products` — 10 columns
- `platform.purpose_of_use_log` — 15 columns
- `platform.resource_types` — 8 columns
- `platform.role_permissions` — 4 columns
- `platform.roles` — 13 columns
- `platform.tenant_product_subscriptions` — 7 columns
- `platform.tenant_quotas` — 8 columns
- `platform.tenants` — 26 columns
- `platform.user_devices` — 10 columns
- `platform.user_permission_overrides` — 14 columns
- `platform.user_sessions` — 13 columns
- `platform.user_tenant_roles` — 11 columns
- `platform.users` — 26 columns

### platform_api.* (8 tables)

- `platform_api.api_client_scopes` — 5 columns
- `platform_api.api_clients` — 26 columns
- `platform_api.api_event_types` — 9 columns
- `platform_api.api_requests` — 14 columns
- `platform_api.api_scopes` — 9 columns
- `platform_api.api_tokens` — 14 columns
- `platform_api.webhook_deliveries` — 16 columns
- `platform_api.webhook_subscriptions` — 18 columns

### docslot.* (27 tables)

- `docslot.abdm_consents` — 14 columns
- `docslot.abdm_health_records` — 12 columns
- `docslot.booking_status_history` — 9 columns
- `docslot.bookings` — 28 columns
- `docslot.conversations` — 10 columns
- `docslot.departments` — 10 columns
- `docslot.doctor_schedules` — 10 columns
- `docslot.doctors` — 31 columns
- `docslot.drug_alerts` — 13 columns
- `docslot.family_members` — 6 columns
- `docslot.healthcare_facilities` — 13 columns
- `docslot.lab_reports` — 20 columns
- `docslot.opd_tokens` — 11 columns
- `docslot.outbox_messages` — 14 columns
- `docslot.patient_medical_history` — 15 columns
- `docslot.patient_tenant_links` — 8 columns
- `docslot.patients` — 30 columns
- `docslot.prescriptions` — 20 columns
- `docslot.procedure_catalog` — 24 columns
- `docslot.processed_messages` — 2 columns
- `docslot.reviews` — 14 columns
- `docslot.schedule_overrides` — 8 columns
- `docslot.test_catalog` — 16 columns
- `docslot.time_slots` — 10 columns
- `docslot.wa_contact_profiles` — 20 columns
- `docslot.wa_message_log` — 16 columns
- `docslot.waitlist` — 10 columns

### ai.* (10 tables)

- `ai.ai_agent_runs` — 32 columns
- `ai.ai_agent_steps` — 20 columns
- `ai.ai_document_extractions` — 23 columns
- `ai.ai_feedback` — 10 columns
- `ai.ai_knowledge_bases` — 13 columns
- `ai.ai_model_configs` — 22 columns
- `ai.ai_predictions` — 14 columns
- `ai.ai_prompts` — 19 columns
- `ai.ai_workflows` — 19 columns
- `ai.embeddings` — 16 columns

### commission.* (10 tables)

- `commission.attribution_disputes` — 16 columns
- `commission.attributions` — 24 columns
- `commission.broker_campaigns` — 19 columns
- `commission.broker_tenant_links` — 17 columns
- `commission.broker_wallets` — 12 columns
- `commission.brokers` — 35 columns
- `commission.commission_rules` — 27 columns
- `commission.payouts` — 28 columns
- `commission.referral_clicks` — 10 columns
- `commission.referral_links` — 17 columns

### ruralreach.* (8 tables)

- `ruralreach.cold_chain_logs` — 7 columns
- `ruralreach.collection_requests` — 28 columns
- `ruralreach.route_stops` — 7 columns
- `ruralreach.routes` — 13 columns
- `ruralreach.sample_chain_of_custody` — 12 columns
- `ruralreach.service_zones` — 8 columns
- `ruralreach.technicians` — 14 columns
- `ruralreach.vehicles` — 18 columns

### safeher.* (8 tables)

- `safeher.anonymous_bookings` — 9 columns
- `safeher.chaperone_bookings` — 19 columns
- `safeher.cultural_preferences` — 8 columns
- `safeher.female_friendly_reviews` — 12 columns
- `safeher.female_only_facilities` — 10 columns
- `safeher.female_only_slots` — 8 columns
- `safeher.female_staff` — 15 columns
- `safeher.patient_privacy_preferences` — 11 columns

### genericfirst.* (6 tables)

- `genericfirst.bioequivalence_studies` — 14 columns
- `genericfirst.drug_equivalence` — 8 columns
- `genericfirst.drug_interactions` — 8 columns
- `genericfirst.drugs` — 22 columns
- `genericfirst.pharmacy_availability` — 7 columns
- `genericfirst.prescription_suggestions` — 10 columns

---

## Full Column Reference

## Schema: `platform`

> Cross-product platform: identity, RBAC (menus + overrides), audit, encryption, billing
> Source: database/01 + 05 + 08

### `platform.access_policies`

Beyond table-level RBAC, sensitive columns need column-level restrictions.

_Source: `database/05_security_hardening.sql` · 11 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `policy_id` | `UUID` | PK, default |
| 2 | `policy_name` | `VARCHAR(100)` | NOT NULL |
| 3 | `schema_name` | `VARCHAR(50)` | NOT NULL |
| 4 | `table_name` | `VARCHAR(50)` | NOT NULL |
| 5 | `column_name` | `VARCHAR(50)` | — |
| 6 | `required_permission` | `VARCHAR(150)` | NOT NULL |
| 7 | `additional_conditions` | `JSONB` | — |
| 8 | `mask_strategy` | `VARCHAR(50)` | — |
| 9 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 10 | `created_by_user_id` | `UUID` | FK→platform.users |
| 11 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.action_types`

TABLE R2: ACTION_TYPES (lookup — what can be done).

_Source: `database/08_rbac_navigation.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `action_type_id` | `UUID` | PK, default |
| 2 | `action_key` | `VARCHAR(30)` | NOT NULL, UNIQUE |
| 3 | `action_name` | `VARCHAR(100)` | NOT NULL |
| 4 | `description` | `TEXT` | — |
| 5 | `is_dangerous` | `BOOLEAN` | NOT NULL, default |
| 6 | `display_order` | `INT` | NOT NULL, default |
| 7 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 8 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.alerts`

TABLE 15: ALERTS (system alert deduplication and history).

_Source: `database/01_platform_core.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `alert_id` | `UUID` | PK, default |
| 2 | `code` | `VARCHAR(50)` | NOT NULL |
| 3 | `severity` | `VARCHAR(20)` | NOT NULL, default |
| 4 | `message` | `TEXT` | NOT NULL |
| 5 | `tenant_id` | `UUID` | FK→platform.tenants |
| 6 | `product_id` | `UUID` | FK→platform.products |
| 7 | `metadata` | `JSONB` | NOT NULL, default |
| 8 | `sent_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `sent_channels` | `VARCHAR(100)` | — |
| 10 | `acknowledged_at` | `TIMESTAMPTZ` | — |
| 11 | `acknowledged_by` | `UUID` | FK→platform.users |
| 12 | `resolved_at` | `TIMESTAMPTZ` | — |

### `platform.anomaly_events`

S10.

_Source: `database/05_security_hardening.sql` · 15 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `anomaly_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `anomaly_type` | `VARCHAR(50)` | NOT NULL |
| 5 | `severity` | `VARCHAR(20)` | NOT NULL |
| 6 | `detected_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 7 | `description` | `TEXT` | NOT NULL |
| 8 | `evidence` | `JSONB` | NOT NULL, default |
| 9 | `ip_address` | `INET` | — |
| 10 | `device_id` | `UUID` | FK→platform.user_devices |
| 11 | `auto_action_taken` | `VARCHAR(50)` | — |
| 12 | `requires_review` | `BOOLEAN` | NOT NULL, default |
| 13 | `reviewed_at` | `TIMESTAMPTZ` | — |
| 14 | `reviewed_by_user_id` | `UUID` | FK→platform.users |
| 15 | `review_outcome` | `VARCHAR(30)` | — |

### `platform.audit_anchors`

S5.

_Source: `database/05_security_hardening.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `anchor_id` | `UUID` | PK, default |
| 2 | `chain_head_sequence` | `BIGINT` | NOT NULL |
| 3 | `chain_head_hash` | `VARCHAR(64)` | NOT NULL |
| 4 | `anchored_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 5 | `anchor_type` | `VARCHAR(30)` | NOT NULL |
| 6 | `anchor_reference` | `VARCHAR(500)` | NOT NULL |
| 7 | `anchored_by_user_id` | `UUID` | FK→platform.users |
| 8 | `metadata` | `JSONB` | default |

### `platform.audit_chain`

Standard audit_log can be tampered with by a DB admin.

_Source: `database/05_security_hardening.sql` · 6 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `chain_id` | `UUID` | PK, default |
| 2 | `audit_id` | `UUID` | FK→platform.audit_log, NOT NULL, UNIQUE |
| 3 | `sequence_number` | `BIGSERIAL` | NOT NULL, UNIQUE |
| 4 | `previous_hash` | `VARCHAR(64)` | NOT NULL |
| 5 | `row_hash` | `VARCHAR(64)` | NOT NULL |
| 6 | `chained_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.audit_log`

Captures every read/write to sensitive data.

_Source: `database/01_platform_core.sql` · 22 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `audit_id` | `UUID` | PK, default |
| 2 | `occurred_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 3 | `user_id` | `UUID` | FK→platform.users |
| 4 | `api_client_id` | `UUID` | — |
| 5 | `impersonator_user_id` | `UUID` | FK→platform.users |
| 6 | `ip_address` | `INET` | — |
| 7 | `user_agent` | `TEXT` | — |
| 8 | `correlation_id` | `VARCHAR(100)` | — |
| 9 | `tenant_id` | `UUID` | FK→platform.tenants |
| 10 | `product_id` | `UUID` | FK→platform.products |
| 11 | `action` | `VARCHAR(50)` | NOT NULL |
| 12 | `resource_type` | `VARCHAR(50)` | NOT NULL |
| 13 | `resource_id` | `UUID` | — |
| 14 | `resource_label` | `VARCHAR(200)` | — |
| 15 | `before_data` | `JSONB` | — |
| 16 | `after_data` | `JSONB` | — |
| 17 | `change_summary` | `TEXT` | — |
| 18 | `purpose` | `VARCHAR(200)` | — |
| 19 | `legal_basis` | `VARCHAR(50)` | — |
| 20 | `success` | `BOOLEAN` | NOT NULL, default |
| 21 | `error_code` | `VARCHAR(50)` | — |
| 22 | `error_message` | `TEXT` | — |

### `platform.breach_log`

TABLE 14: BREACH_LOG (security incident reporting).

_Source: `database/01_platform_core.sql` · 20 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `breach_id` | `UUID` | PK, default |
| 2 | `breach_type` | `VARCHAR(50)` | NOT NULL |
| 3 | `severity` | `VARCHAR(20)` | NOT NULL |
| 4 | `description` | `TEXT` | NOT NULL |
| 5 | `affected_tenant_ids` | `UUID[]` | — |
| 6 | `affected_user_ids` | `UUID[]` | — |
| 7 | `affected_record_count` | `INT` | — |
| 8 | `affected_data_categories` | `VARCHAR(100)[]` | — |
| 9 | `detected_at` | `TIMESTAMPTZ` | NOT NULL |
| 10 | `detected_by` | `VARCHAR(100)` | — |
| 11 | `detection_method` | `VARCHAR(100)` | — |
| 12 | `reported_to_dpb_at` | `TIMESTAMPTZ` | — |
| 13 | `reported_to_users_at` | `TIMESTAMPTZ` | — |
| 14 | `containment_actions` | `TEXT` | — |
| 15 | `root_cause` | `TEXT` | — |
| 16 | `remediation` | `TEXT` | — |
| 17 | `resolved_at` | `TIMESTAMPTZ` | — |
| 18 | `resolved_by` | `UUID` | FK→platform.users |
| 19 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 20 | `created_by` | `UUID` | FK→platform.users |

### `platform.consent_event_log`

DPDP requires proof of consent.

_Source: `database/05_security_hardening.sql` · 17 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `event_id` | `UUID` | PK, default |
| 2 | `consent_id` | `UUID` | FK→docslot.abdm_consents |
| 3 | `patient_phone` | `VARCHAR(15)` | NOT NULL |
| 4 | `tenant_id` | `UUID` | FK→platform.tenants |
| 5 | `event_type` | `VARCHAR(30)` | NOT NULL |
| 6 | `consent_scope` | `JSONB` | NOT NULL |
| 7 | `legal_basis` | `VARCHAR(50)` | — |
| 8 | `legal_basis_details` | `TEXT` | — |
| 9 | `channel` | `VARCHAR(30)` | — |
| 10 | `channel_message_id` | `VARCHAR(100)` | — |
| 11 | `actor_user_id` | `UUID` | FK→platform.users |
| 12 | `actor_api_client_id` | `UUID` | FK→platform_api.api_clients |
| 13 | `ip_address` | `INET` | — |
| 14 | `user_agent` | `TEXT` | — |
| 15 | `occurred_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `downstream_notified_at` | `TIMESTAMPTZ` | — |
| 17 | `downstream_notification_status` | `VARCHAR(20)` | — |

### `platform.data_deletion_requests`

TABLE 18: DATA_DELETION_REQUESTS (DPDP right-to-erasure tracking).

_Source: `database/01_platform_core.sql` · 17 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `request_id` | `UUID` | PK, default |
| 2 | `requester_type` | `VARCHAR(20)` | NOT NULL |
| 3 | `requester_user_id` | `UUID` | FK→platform.users |
| 4 | `requester_email` | `CITEXT` | — |
| 5 | `requester_phone` | `VARCHAR(15)` | — |
| 6 | `subject_user_id` | `UUID` | FK→platform.users |
| 7 | `subject_phone` | `VARCHAR(15)` | — |
| 8 | `tenant_ids` | `UUID[]` | — |
| 9 | `scope` | `VARCHAR(20)` | NOT NULL, default |
| 10 | `products_affected` | `VARCHAR(50)[]` | — |
| 11 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 12 | `reason` | `TEXT` | — |
| 13 | `rejection_reason` | `TEXT` | — |
| 14 | `grace_period_ends_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 15 | `processed_at` | `TIMESTAMPTZ` | — |
| 16 | `processed_by` | `UUID` | FK→platform.users |
| 17 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.data_export_requests`

S11.

_Source: `database/05_security_hardening.sql` · 24 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `request_id` | `UUID` | PK, default |
| 2 | `requester_user_id` | `UUID` | FK→platform.users |
| 3 | `requester_email` | `CITEXT` | — |
| 4 | `requester_phone` | `VARCHAR(15)` | — |
| 5 | `subject_phone` | `VARCHAR(15)` | NOT NULL |
| 6 | `scope_product_keys` | `VARCHAR(50)[]` | NOT NULL, default |
| 7 | `scope_data_classes` | `VARCHAR(50)[]` | default |
| 8 | `scope_date_from` | `TIMESTAMPTZ` | — |
| 9 | `scope_date_to` | `TIMESTAMPTZ` | — |
| 10 | `export_format` | `VARCHAR(20)` | NOT NULL, default |
| 11 | `verification_method` | `VARCHAR(30)` | — |
| 12 | `verified_at` | `TIMESTAMPTZ` | — |
| 13 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 14 | `processing_started_at` | `TIMESTAMPTZ` | — |
| 15 | `processing_completed_at` | `TIMESTAMPTZ` | — |
| 16 | `download_url` | `TEXT` | — |
| 17 | `download_expires_at` | `TIMESTAMPTZ` | — |
| 18 | `downloaded_at` | `TIMESTAMPTZ` | — |
| 19 | `download_ip` | `INET` | — |
| 20 | `encryption_key_hint` | `VARCHAR(200)` | — |
| 21 | `file_size_bytes` | `BIGINT` | — |
| 22 | `file_checksum` | `VARCHAR(64)` | — |
| 23 | `rejection_reason` | `TEXT` | — |
| 24 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.deletion_certificates`

When a patient requests deletion, we destroy the encryption key for their.

_Source: `database/05_security_hardening.sql` · 18 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `certificate_id` | `UUID` | PK, default |
| 2 | `deletion_request_id` | `UUID` | FK→platform.data_deletion_requests, NOT NULL |
| 3 | `subject_phone` | `VARCHAR(15)` | NOT NULL |
| 4 | `deleted_record_counts` | `JSONB` | NOT NULL |
| 5 | `affected_tenant_ids` | `UUID[]` | — |
| 6 | `destroyed_key_ids` | `UUID[]` | FK→platform.encryption_keys, NOT NULL |
| 7 | `destruction_method` | `VARCHAR(30)` | NOT NULL |
| 8 | `pre_deletion_hash` | `VARCHAR(64)` | — |
| 9 | `post_deletion_hash` | `VARCHAR(64)` | — |
| 10 | `verification_query` | `TEXT` | — |
| 11 | `certified_by_user_id` | `UUID` | FK→platform.users, NOT NULL |
| 12 | `certified_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 13 | `certificate_pdf_url` | `TEXT` | — |
| 14 | `signature_algorithm` | `VARCHAR(30)` | default |
| 15 | `digital_signature` | `TEXT` | — |
| 16 | `dpb_report_filed_at` | `TIMESTAMPTZ` | — |
| 17 | `retained_for_compliance_until` | `DATE` | — |
| 18 | `metadata` | `JSONB` | default |

### `platform.encrypted_fields_registry`

Documents the encrypted-field schema so application code knows what to.

_Source: `database/05_security_hardening.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `registry_id` | `UUID` | PK, default |
| 2 | `schema_name` | `VARCHAR(50)` | NOT NULL |
| 3 | `table_name` | `VARCHAR(50)` | NOT NULL |
| 4 | `column_name` | `VARCHAR(50)` | NOT NULL |
| 5 | `data_class` | `VARCHAR(50)` | NOT NULL |
| 6 | `encryption_required` | `BOOLEAN` | NOT NULL, default |
| 7 | `is_searchable` | `BOOLEAN` | NOT NULL, default |
| 8 | `pii_category` | `VARCHAR(50)` | — |
| 9 | `legal_basis` | `VARCHAR(50)` | — |
| 10 | `retention_days` | `INT` | — |
| 11 | `notes` | `TEXT` | — |
| 12 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.encryption_keys`

DocSlot encrypts sensitive fields (medical_history, prescriptions, ABHA IDs,.

_Source: `database/05_security_hardening.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `key_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants |
| 3 | `data_class` | `VARCHAR(50)` | NOT NULL |
| 4 | `key_reference` | `VARCHAR(500)` | NOT NULL |
| 5 | `kms_provider` | `VARCHAR(30)` | NOT NULL |
| 6 | `key_algorithm` | `VARCHAR(30)` | NOT NULL, default |
| 7 | `key_version` | `INT` | NOT NULL, default |
| 8 | `activated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `rotated_at` | `TIMESTAMPTZ` | — |
| 10 | `next_rotation_due_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 11 | `deactivated_at` | `TIMESTAMPTZ` | — |
| 12 | `destroyed_at` | `TIMESTAMPTZ` | — |
| 13 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 14 | `created_by_user_id` | `UUID` | FK→platform.users |
| 15 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `metadata` | `JSONB` | NOT NULL, default |

### `platform.ip_allowlist`

S8.

_Source: `database/05_security_hardening.sql` · 9 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `allowlist_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants |
| 3 | `user_id` | `UUID` | FK→platform.users |
| 4 | `cidr_range` | `CIDR` | NOT NULL |
| 5 | `label` | `VARCHAR(100)` | — |
| 6 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 7 | `created_by_user_id` | `UUID` | FK→platform.users |
| 8 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `expires_at` | `TIMESTAMPTZ` | — |

### `platform.key_usage_log`

For forensics and key compromise detection.

_Source: `database/05_security_hardening.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `usage_id` | `UUID` | PK, default |
| 2 | `key_id` | `UUID` | FK→platform.encryption_keys, NOT NULL |
| 3 | `operation` | `VARCHAR(20)` | NOT NULL |
| 4 | `user_id` | `UUID` | FK→platform.users |
| 5 | `api_client_id` | `UUID` | FK→platform_api.api_clients |
| 6 | `tenant_id` | `UUID` | FK→platform.tenants |
| 7 | `resource_type` | `VARCHAR(50)` | — |
| 8 | `resource_id` | `UUID` | — |
| 9 | `ip_address` | `INET` | — |
| 10 | `success` | `BOOLEAN` | NOT NULL, default |
| 11 | `error_message` | `TEXT` | — |
| 12 | `occurred_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.login_attempts`

TABLE 10: LOGIN_ATTEMPTS (rate limiting + lockout).

_Source: `database/01_platform_core.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `attempt_id` | `UUID` | PK, default |
| 2 | `email` | `CITEXT` | NOT NULL |
| 3 | `ip_address` | `INET` | NOT NULL |
| 4 | `user_agent` | `TEXT` | — |
| 5 | `success` | `BOOLEAN` | NOT NULL |
| 6 | `failure_reason` | `VARCHAR(100)` | — |
| 7 | `attempted_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.menu_permissions`

A menu item is shown to a user only if they hold at least one of the.

_Source: `database/08_rbac_navigation.sql` · 5 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `menu_permission_id` | `UUID` | PK, default |
| 2 | `menu_id` | `UUID` | FK→platform.navigation_menus, NOT NULL |
| 3 | `permission_id` | `UUID` | FK→platform.permissions, NOT NULL |
| 4 | `require_all` | `BOOLEAN` | NOT NULL, default |
| 5 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.navigation_menus`

The application's menu structure lives in the database, not hardcoded in the.

_Source: `database/08_rbac_navigation.sql` · 18 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `menu_id` | `UUID` | PK, default |
| 2 | `parent_menu_id` | `UUID` | FK→platform.navigation_menus |
| 3 | `menu_key` | `VARCHAR(80)` | NOT NULL |
| 4 | `menu_label` | `VARCHAR(120)` | NOT NULL, default |
| 5 | `menu_label_hi` | `VARCHAR(120)` | — |
| 6 | `menu_icon` | `VARCHAR(80)` | — |
| 7 | `menu_url` | `VARCHAR(255)` | — |
| 8 | `product_key` | `VARCHAR(50)` | NOT NULL, default |
| 9 | `applies_to_tenant_types` | `VARCHAR(30)[]` | — |
| 10 | `display_order` | `INT` | NOT NULL, default |
| 11 | `is_section_header` | `BOOLEAN` | NOT NULL, default |
| 12 | `badge_source` | `VARCHAR(50)` | — |
| 13 | `opens_in_new_tab` | `BOOLEAN` | NOT NULL, default |
| 14 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 15 | `is_system` | `BOOLEAN` | NOT NULL, default |
| 16 | `tenant_id` | `UUID` | FK→platform.tenants |
| 17 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 18 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.notifications`

TABLE 16: NOTIFICATIONS (in-app dashboard notifications).

_Source: `database/01_platform_core.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `notification_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `product_id` | `UUID` | FK→platform.products |
| 5 | `type` | `VARCHAR(50)` | NOT NULL |
| 6 | `severity` | `VARCHAR(20)` | NOT NULL, default |
| 7 | `title` | `VARCHAR(200)` | NOT NULL |
| 8 | `message` | `TEXT` | NOT NULL |
| 9 | `action_url` | `VARCHAR(500)` | — |
| 10 | `metadata` | `JSONB` | NOT NULL, default |
| 11 | `is_read` | `BOOLEAN` | NOT NULL, default |
| 12 | `read_at` | `TIMESTAMPTZ` | — |
| 13 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 14 | `expires_at` | `TIMESTAMPTZ` | default |

### `platform.password_reset_tokens`

_Source: `database/01_platform_core.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `token_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `token_hash` | `VARCHAR(64)` | NOT NULL |
| 4 | `requested_ip` | `INET` | — |
| 5 | `expires_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 6 | `used_at` | `TIMESTAMPTZ` | — |
| 7 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.permissions`

This is the foundation of the entire RBAC system.

_Source: `database/01_platform_core.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `permission_id` | `UUID` | PK, default |
| 2 | `permission_key` | `VARCHAR(150)` | NOT NULL, UNIQUE |
| 3 | `product_id` | `UUID` | FK→platform.products |
| 4 | `resource` | `VARCHAR(50)` | NOT NULL |
| 5 | `action` | `VARCHAR(30)` | NOT NULL |
| 6 | `scope` | `VARCHAR(20)` | NOT NULL, default |
| 7 | `description` | `TEXT` | NOT NULL |
| 8 | `is_system` | `BOOLEAN` | NOT NULL, default |
| 9 | `is_dangerous` | `BOOLEAN` | NOT NULL, default |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.platform_settings`

TABLE 12: PLATFORM_SETTINGS (super admin runtime config).

_Source: `database/01_platform_core.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `setting_key` | `VARCHAR(150)` | PK |
| 2 | `setting_value` | `TEXT` | NOT NULL |
| 3 | `value_type` | `VARCHAR(20)` | NOT NULL, default |
| 4 | `category` | `VARCHAR(50)` | NOT NULL |
| 5 | `is_encrypted` | `BOOLEAN` | NOT NULL, default |
| 6 | `description` | `TEXT` | — |
| 7 | `updated_by` | `UUID` | FK→platform.users |
| 8 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.products`

Every product (DocSlot, RuralReach, etc.

_Source: `database/01_platform_core.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `product_id` | `UUID` | PK, default |
| 2 | `product_key` | `VARCHAR(50)` | NOT NULL, UNIQUE |
| 3 | `name` | `VARCHAR(100)` | NOT NULL |
| 4 | `description` | `TEXT` | — |
| 5 | `schema_name` | `VARCHAR(50)` | NOT NULL |
| 6 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 7 | `requires_features` | `VARCHAR(100)[]` | — |
| 8 | `metadata` | `JSONB` | NOT NULL, default |
| 9 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 10 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.purpose_of_use_log`

When a doctor opens a patient's record, they must declare WHY they're.

_Source: `database/05_security_hardening.sql` · 15 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `log_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `session_id` | `UUID` | FK→platform.user_sessions |
| 5 | `accessed_resource_type` | `VARCHAR(50)` | NOT NULL |
| 6 | `accessed_resource_id` | `UUID` | NOT NULL |
| 7 | `declared_purpose` | `VARCHAR(50)` | NOT NULL |
| 8 | `purpose_notes` | `TEXT` | — |
| 9 | `is_break_glass` | `BOOLEAN` | NOT NULL, default |
| 10 | `break_glass_reason` | `TEXT` | — |
| 11 | `accessed_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 12 | `review_required` | `BOOLEAN` | NOT NULL, default |
| 13 | `reviewed_at` | `TIMESTAMPTZ` | — |
| 14 | `reviewed_by_user_id` | `UUID` | FK→platform.users |
| 15 | `review_outcome` | `VARCHAR(20)` | — |

### `platform.resource_types`

Optional normalization of the permissions.

_Source: `database/08_rbac_navigation.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `resource_type_id` | `UUID` | PK, default |
| 2 | `resource_key` | `VARCHAR(50)` | NOT NULL, UNIQUE |
| 3 | `resource_name` | `VARCHAR(100)` | NOT NULL |
| 4 | `product_id` | `UUID` | FK→platform.products |
| 5 | `description` | `TEXT` | — |
| 6 | `display_order` | `INT` | NOT NULL, default |
| 7 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 8 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.role_permissions`

TABLE 6: ROLE_PERMISSIONS (the matrix as data, not code).

_Source: `database/01_platform_core.sql` · 4 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `role_id` | `UUID` | FK→platform.roles, NOT NULL |
| 2 | `permission_id` | `UUID` | FK→platform.permissions, NOT NULL |
| 3 | `granted_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 4 | `granted_by` | `UUID` | FK→users |

### `platform.roles`

Roles bundle permissions together for assignment to users.

_Source: `database/01_platform_core.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `role_id` | `UUID` | PK, default |
| 2 | `role_key` | `VARCHAR(50)` | NOT NULL |
| 3 | `name` | `VARCHAR(100)` | NOT NULL |
| 4 | `description` | `TEXT` | — |
| 5 | `product_id` | `UUID` | FK→platform.products |
| 6 | `tenant_id` | `UUID` | FK→platform.tenants |
| 7 | `scope` | `VARCHAR(20)` | NOT NULL, default |
| 8 | `is_system` | `BOOLEAN` | NOT NULL, default |
| 9 | `is_default` | `BOOLEAN` | NOT NULL, default |
| 10 | `metadata` | `JSONB` | NOT NULL, default |
| 11 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 12 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 13 | `deleted_at` | `TIMESTAMPTZ` | — |

### `platform.tenant_product_subscriptions`

TABLE 3: TENANT_PRODUCT_SUBSCRIPTIONS (which products each tenant uses).

_Source: `database/01_platform_core.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `subscription_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `product_id` | `UUID` | FK→platform.products, NOT NULL |
| 4 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 5 | `activated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 6 | `cancelled_at` | `TIMESTAMPTZ` | — |
| 7 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.tenant_quotas`

TABLE 17: TENANT_QUOTAS (per-tenant resource limits).

_Source: `database/01_platform_core.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 2 | `product_id` | `UUID` | FK→platform.products, NOT NULL |
| 3 | `quota_key` | `VARCHAR(100)` | NOT NULL |
| 4 | `quota_value` | `BIGINT` | — |
| 5 | `current_usage` | `BIGINT` | NOT NULL, default |
| 6 | `reset_period` | `VARCHAR(20)` | — |
| 7 | `last_reset_at` | `TIMESTAMPTZ` | — |
| 8 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.tenants`

Renamed to "tenants" because non-DocSlot products may have different.

_Source: `database/01_platform_core.sql` · 26 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `tenant_id` | `UUID` | PK, default |
| 2 | `tenant_code` | `VARCHAR(20)` | NOT NULL, UNIQUE |
| 3 | `legal_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `display_name` | `VARCHAR(200)` | NOT NULL |
| 5 | `tenant_type` | `VARCHAR(30)` | NOT NULL |
| 6 | `primary_email` | `CITEXT` | NOT NULL |
| 7 | `primary_phone` | `VARCHAR(15)` | NOT NULL |
| 8 | `website` | `VARCHAR(200)` | — |
| 9 | `address_line1` | `VARCHAR(200)` | — |
| 10 | `address_line2` | `VARCHAR(200)` | — |
| 11 | `city` | `VARCHAR(100)` | — |
| 12 | `state` | `VARCHAR(100)` | — |
| 13 | `country` | `VARCHAR(2)` | NOT NULL, default |
| 14 | `pin_code` | `VARCHAR(10)` | — |
| 15 | `timezone` | `VARCHAR(50)` | NOT NULL, default |
| 16 | `gstin` | `VARCHAR(15)` | — |
| 17 | `pan` | `VARCHAR(10)` | — |
| 18 | `cin` | `VARCHAR(21)` | — |
| 19 | `regulatory_metadata` | `JSONB` | NOT NULL, default |
| 20 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 21 | `trial_ends_at` | `TIMESTAMPTZ` | — |
| 22 | `suspended_reason` | `TEXT` | — |
| 23 | `settings` | `JSONB` | NOT NULL, default |
| 24 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 25 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 26 | `deleted_at` | `TIMESTAMPTZ` | — |

### `platform.user_devices`

S9.

_Source: `database/05_security_hardening.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `device_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `device_fingerprint` | `VARCHAR(255)` | NOT NULL |
| 4 | `device_label` | `VARCHAR(100)` | — |
| 5 | `last_ip_address` | `INET` | — |
| 6 | `last_user_agent` | `TEXT` | — |
| 7 | `last_seen_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 8 | `first_seen_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `trust_level` | `VARCHAR(20)` | NOT NULL, default |
| 10 | `trusted_by_user_at` | `TIMESTAMPTZ` | — |

### `platform.user_permission_overrides`

The "exceptional case" mechanism.

_Source: `database/08_rbac_navigation.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `override_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `permission_id` | `UUID` | FK→platform.permissions, NOT NULL |
| 4 | `tenant_id` | `UUID` | FK→platform.tenants |
| 5 | `is_allowed` | `BOOLEAN` | NOT NULL |
| 6 | `reason` | `TEXT` | NOT NULL |
| 7 | `granted_by_user_id` | `UUID` | FK→platform.users |
| 8 | `effective_from` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `expires_at` | `TIMESTAMPTZ` | — |
| 10 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 11 | `revoked_at` | `TIMESTAMPTZ` | — |
| 12 | `revoked_by_user_id` | `UUID` | FK→platform.users |
| 13 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 14 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform.user_sessions`

TABLE 9: USER_SESSIONS (JWT token tracking for revocation).

_Source: `database/01_platform_core.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `session_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `token_hash` | `VARCHAR(64)` | NOT NULL |
| 4 | `refresh_token_hash` | `VARCHAR(64)` | — |
| 5 | `active_tenant_id` | `UUID` | FK→platform.tenants |
| 6 | `device_info` | `VARCHAR(500)` | — |
| 7 | `ip_address` | `INET` | — |
| 8 | `issued_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `expires_at` | `TIMESTAMPTZ` | NOT NULL |
| 10 | `refresh_expires_at` | `TIMESTAMPTZ` | — |
| 11 | `last_activity_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 12 | `revoked_at` | `TIMESTAMPTZ` | — |
| 13 | `revoked_reason` | `VARCHAR(100)` | — |

### `platform.user_tenant_roles`

This is where access control is actually enforced.

_Source: `database/01_platform_core.sql` · 11 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `user_tenant_role_id` | `UUID` | PK, default |
| 2 | `user_id` | `UUID` | FK→platform.users, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `role_id` | `UUID` | FK→platform.roles, NOT NULL |
| 5 | `is_primary` | `BOOLEAN` | NOT NULL, default |
| 6 | `granted_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 7 | `granted_by` | `UUID` | FK→platform.users |
| 8 | `expires_at` | `TIMESTAMPTZ` | — |
| 9 | `revoked_at` | `TIMESTAMPTZ` | — |
| 10 | `revoked_by` | `UUID` | FK→platform.users |
| 11 | `revoked_reason` | `VARCHAR(200)` | — |

### `platform.users`

A user can belong to multiple tenants with different roles in each.

_Source: `database/01_platform_core.sql` · 26 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `user_id` | `UUID` | PK, default |
| 2 | `email` | `CITEXT` | NOT NULL, UNIQUE |
| 3 | `phone` | `VARCHAR(15)` | — |
| 4 | `password_hash` | `VARCHAR(255)` | — |
| 5 | `full_name` | `VARCHAR(200)` | NOT NULL |
| 6 | `email_verified` | `BOOLEAN` | NOT NULL, default |
| 7 | `phone_verified` | `BOOLEAN` | NOT NULL, default |
| 8 | `mfa_enabled` | `BOOLEAN` | NOT NULL, default |
| 9 | `mfa_secret` | `VARCHAR(255)` | — |
| 10 | `sso_provider` | `VARCHAR(50)` | — |
| 11 | `sso_subject` | `VARCHAR(200)` | — |
| 12 | `last_login_at` | `TIMESTAMPTZ` | — |
| 13 | `last_login_ip` | `INET` | — |
| 14 | `failed_login_count` | `SMALLINT` | NOT NULL, default |
| 15 | `locked_until` | `TIMESTAMPTZ` | — |
| 16 | `password_changed_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 17 | `must_change_password` | `BOOLEAN` | NOT NULL, default |
| 18 | `preferred_language` | `VARCHAR(10)` | NOT NULL, default |
| 19 | `timezone` | `VARCHAR(50)` | NOT NULL, default |
| 20 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 21 | `is_platform_user` | `BOOLEAN` | NOT NULL, default |
| 22 | `accepted_terms_at` | `TIMESTAMPTZ` | — |
| 23 | `accepted_terms_version` | `VARCHAR(20)` | — |
| 24 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 25 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 26 | `deleted_at` | `TIMESTAMPTZ` | — |

---

## Schema: `platform_api`

> Platform-as-a-Service: OAuth 2.0, scoped JWTs, webhooks
> Source: database/02

### `platform_api.api_client_scopes`

TABLE 21: API_CLIENT_SCOPES (which scopes each API client can request).

_Source: `database/02_platform_api.sql` · 5 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `client_id` | `UUID` | FK→platform_api.api_clients, NOT NULL |
| 2 | `scope_id` | `UUID` | FK→platform_api.api_scopes, NOT NULL |
| 3 | `granted_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 4 | `granted_by` | `UUID` | FK→platform.users |
| 5 | `expires_at` | `TIMESTAMPTZ` | — |

### `platform_api.api_clients`

TABLE 19: API_CLIENTS (third-party applications registered with platform).

_Source: `database/02_platform_api.sql` · 26 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `client_id` | `UUID` | PK, default |
| 2 | `client_code` | `VARCHAR(50)` | NOT NULL, UNIQUE |
| 3 | `client_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `client_secret_hash` | `VARCHAR(255)` | NOT NULL |
| 5 | `client_type` | `VARCHAR(30)` | NOT NULL |
| 6 | `owner_tenant_id` | `UUID` | FK→platform.tenants |
| 7 | `owner_email` | `CITEXT` | NOT NULL |
| 8 | `owner_organization` | `VARCHAR(200)` | — |
| 9 | `grant_types` | `VARCHAR(50)[]` | NOT NULL, default |
| 10 | `redirect_uris` | `VARCHAR(500)[]` | — |
| 11 | `allowed_origins` | `VARCHAR(200)[]` | — |
| 12 | `rate_limit_per_minute` | `INT` | NOT NULL, default |
| 13 | `rate_limit_per_day` | `INT` | NOT NULL, default |
| 14 | `burst_limit` | `INT` | NOT NULL, default |
| 15 | `webhook_signing_secret` | `VARCHAR(255)` | — |
| 16 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 17 | `is_verified` | `BOOLEAN` | NOT NULL, default |
| 18 | `verified_at` | `TIMESTAMPTZ` | — |
| 19 | `verified_by` | `UUID` | FK→platform.users |
| 20 | `purpose` | `TEXT` | NOT NULL |
| 21 | `data_protection_agreement_url` | `VARCHAR(500)` | — |
| 22 | `data_protection_signed_at` | `TIMESTAMPTZ` | — |
| 23 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 24 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 25 | `last_used_at` | `TIMESTAMPTZ` | — |
| 26 | `deleted_at` | `TIMESTAMPTZ` | — |

### `platform_api.api_event_types`

TABLE 26: API_EVENT_TYPES (registry of all events that can be subscribed to).

_Source: `database/02_platform_api.sql` · 9 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `event_type` | `VARCHAR(100)` | PK |
| 2 | `product_id` | `UUID` | FK→platform.products |
| 3 | `resource` | `VARCHAR(50)` | NOT NULL |
| 4 | `action` | `VARCHAR(30)` | NOT NULL |
| 5 | `description` | `TEXT` | NOT NULL |
| 6 | `payload_schema` | `JSONB` | — |
| 7 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 8 | `requires_scope` | `VARCHAR(100)` | — |
| 9 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform_api.api_requests`

TABLE 23: API_REQUESTS (request log for rate limiting + analytics).

_Source: `database/02_platform_api.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `request_id` | `UUID` | PK, default |
| 2 | `client_id` | `UUID` | FK→platform_api.api_clients |
| 3 | `token_id` | `UUID` | FK→platform_api.api_tokens |
| 4 | `tenant_id` | `UUID` | FK→platform.tenants |
| 5 | `method` | `VARCHAR(10)` | NOT NULL |
| 6 | `path` | `VARCHAR(500)` | NOT NULL |
| 7 | `ip_address` | `INET` | — |
| 8 | `user_agent` | `TEXT` | — |
| 9 | `status_code` | `INT` | NOT NULL |
| 10 | `response_time_ms` | `INT` | — |
| 11 | `response_size_bytes` | `INT` | — |
| 12 | `error_code` | `VARCHAR(50)` | — |
| 13 | `error_message` | `TEXT` | — |
| 14 | `occurred_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform_api.api_scopes`

Scopes are like permissions but specifically for API access.

_Source: `database/02_platform_api.sql` · 9 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `scope_id` | `UUID` | PK, default |
| 2 | `scope_key` | `VARCHAR(100)` | NOT NULL, UNIQUE |
| 3 | `product_id` | `UUID` | FK→platform.products |
| 4 | `resource` | `VARCHAR(50)` | NOT NULL |
| 5 | `action` | `VARCHAR(30)` | NOT NULL |
| 6 | `description` | `TEXT` | NOT NULL |
| 7 | `is_dangerous` | `BOOLEAN` | NOT NULL, default |
| 8 | `requires_consent` | `BOOLEAN` | NOT NULL, default |
| 9 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `platform_api.api_tokens`

TABLE 22: API_TOKENS (issued JWT tokens for clients).

_Source: `database/02_platform_api.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `token_id` | `UUID` | PK, default |
| 2 | `client_id` | `UUID` | FK→platform_api.api_clients, NOT NULL |
| 3 | `token_hash` | `VARCHAR(64)` | NOT NULL, UNIQUE |
| 4 | `requested_scopes` | `VARCHAR(100)[]` | NOT NULL |
| 5 | `granted_scopes` | `VARCHAR(100)[]` | NOT NULL |
| 6 | `tenant_id` | `UUID` | FK→platform.tenants |
| 7 | `user_id` | `UUID` | FK→platform.users |
| 8 | `issued_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `expires_at` | `TIMESTAMPTZ` | NOT NULL |
| 10 | `last_used_at` | `TIMESTAMPTZ` | — |
| 11 | `use_count` | `BIGINT` | NOT NULL, default |
| 12 | `revoked_at` | `TIMESTAMPTZ` | — |
| 13 | `revoked_by` | `UUID` | FK→platform.users |
| 14 | `revoked_reason` | `VARCHAR(200)` | — |

### `platform_api.webhook_deliveries`

TABLE 25: WEBHOOK_DELIVERIES (delivery attempts and history).

_Source: `database/02_platform_api.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `delivery_id` | `UUID` | PK, default |
| 2 | `webhook_id` | `UUID` | FK→platform_api.webhook_subscriptions, NOT NULL |
| 3 | `event_type` | `VARCHAR(100)` | NOT NULL |
| 4 | `event_id` | `UUID` | NOT NULL |
| 5 | `payload` | `JSONB` | NOT NULL |
| 6 | `signature` | `VARCHAR(255)` | — |
| 7 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 8 | `attempt_count` | `SMALLINT` | NOT NULL, default |
| 9 | `response_status_code` | `INT` | — |
| 10 | `response_headers` | `JSONB` | — |
| 11 | `response_body` | `TEXT` | — |
| 12 | `response_time_ms` | `INT` | — |
| 13 | `error_message` | `TEXT` | — |
| 14 | `next_retry_at` | `TIMESTAMPTZ` | — |
| 15 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `delivered_at` | `TIMESTAMPTZ` | — |

### `platform_api.webhook_subscriptions`

TABLE 24: WEBHOOK_SUBSCRIPTIONS (outbound event subscriptions).

_Source: `database/02_platform_api.sql` · 18 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `webhook_id` | `UUID` | PK, default |
| 2 | `client_id` | `UUID` | FK→platform_api.api_clients, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `name` | `VARCHAR(100)` | NOT NULL |
| 5 | `url` | `VARCHAR(500)` | NOT NULL |
| 6 | `secret_hash` | `VARCHAR(255)` | NOT NULL |
| 7 | `event_types` | `VARCHAR(100)[]` | NOT NULL |
| 8 | `filter_expression` | `TEXT` | — |
| 9 | `max_retries` | `SMALLINT` | NOT NULL, default |
| 10 | `retry_backoff` | `VARCHAR(20)` | NOT NULL, default |
| 11 | `timeout_seconds` | `SMALLINT` | NOT NULL, default |
| 12 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 13 | `last_success_at` | `TIMESTAMPTZ` | — |
| 14 | `last_failure_at` | `TIMESTAMPTZ` | — |
| 15 | `consecutive_failures` | `SMALLINT` | NOT NULL, default |
| 16 | `auto_disabled_at` | `TIMESTAMPTZ` | — |
| 17 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 18 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Schema: `docslot`

> DocSlot product: booking, prescriptions, ABDM, WhatsApp state + chat identity
> Source: database/03 + 09

### `docslot.abdm_consents`

_Source: `database/03_docslot.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `consent_id` | `UUID` | PK, default |
| 2 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 3 | `requesting_tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `abdm_consent_request_id` | `VARCHAR(100)` | NOT NULL |
| 5 | `abdm_consent_artifact_id` | `VARCHAR(100)` | — |
| 6 | `purpose` | `VARCHAR(50)` | NOT NULL |
| 7 | `health_info_types` | `VARCHAR(50)[]` | — |
| 8 | `date_range_from` | `DATE` | — |
| 9 | `date_range_to` | `DATE` | — |
| 10 | `expires_at` | `TIMESTAMPTZ` | NOT NULL |
| 11 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 12 | `granted_at` | `TIMESTAMPTZ` | — |
| 13 | `revoked_at` | `TIMESTAMPTZ` | — |
| 14 | `metadata` | `JSONB` | default |

### `docslot.abdm_health_records`

TABLE D25: ABDM_HEALTH_RECORDS (FHIR R4 health records).

_Source: `database/03_docslot.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `record_id` | `UUID` | PK, default |
| 2 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `booking_id` | `UUID` | FK→docslot.bookings |
| 5 | `abha_number` | `VARCHAR(20)` | NOT NULL |
| 6 | `record_type` | `VARCHAR(50)` | NOT NULL |
| 7 | `fhir_bundle` | `JSONB` | NOT NULL |
| 8 | `care_context_id` | `VARCHAR(100)` | — |
| 9 | `is_linked_to_phr` | `BOOLEAN` | NOT NULL, default |
| 10 | `linked_at` | `TIMESTAMPTZ` | — |
| 11 | `consent_id` | `VARCHAR(100)` | — |
| 12 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.booking_status_history`

TABLE D11: BOOKING_STATUS_HISTORY (state transition audit).

_Source: `database/03_docslot.sql` · 9 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `history_id` | `UUID` | PK, default |
| 2 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL |
| 3 | `from_status` | `VARCHAR(20)` | — |
| 4 | `to_status` | `VARCHAR(20)` | NOT NULL |
| 5 | `changed_by_user_id` | `UUID` | FK→platform.users |
| 6 | `changed_via` | `VARCHAR(20)` | — |
| 7 | `reason` | `TEXT` | — |
| 8 | `metadata` | `JSONB` | default |
| 9 | `changed_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.bookings`

TABLE D10: BOOKINGS (the core entity).

_Source: `database/03_docslot.sql` · 28 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `booking_id` | `UUID` | PK, default |
| 2 | `booking_number` | `VARCHAR(20)` | NOT NULL, UNIQUE |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `slot_id` | `UUID` | FK→docslot.time_slots, NOT NULL |
| 5 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 6 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 7 | `department_id` | `UUID` | FK→docslot.departments |
| 8 | `booking_type` | `VARCHAR(20)` | NOT NULL, default |
| 9 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 10 | `patient_name_at_booking` | `VARCHAR(200)` | — |
| 11 | `patient_phone_at_booking` | `VARCHAR(15)` | — |
| 12 | `patient_age_at_booking` | `SMALLINT` | — |
| 13 | `booked_via` | `VARCHAR(20)` | NOT NULL, default |
| 14 | `booked_for` | `VARCHAR(20)` | NOT NULL, default |
| 15 | `booked_for_patient_id` | `UUID` | FK→docslot.patients |
| 16 | `chief_complaint` | `TEXT` | — |
| 17 | `notes` | `TEXT` | — |
| 18 | `cancellation_reason` | `TEXT` | — |
| 19 | `reminder_24h_sent` | `BOOLEAN` | NOT NULL, default |
| 20 | `reminder_1h_sent` | `BOOLEAN` | NOT NULL, default |
| 21 | `booked_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 22 | `confirmed_at` | `TIMESTAMPTZ` | — |
| 23 | `cancelled_at` | `TIMESTAMPTZ` | — |
| 24 | `completed_at` | `TIMESTAMPTZ` | — |
| 25 | `no_show_at` | `TIMESTAMPTZ` | — |
| 26 | `created_by_user_id` | `UUID` | FK→platform.users |
| 27 | `cancelled_by_user_id` | `UUID` | FK→platform.users |
| 28 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.conversations`

TABLE D19: CONVERSATIONS (WhatsApp conversation state).

_Source: `database/03_docslot.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `conversation_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `patient_id` | `UUID` | FK→docslot.patients |
| 4 | `whatsapp_phone` | `VARCHAR(15)` | NOT NULL |
| 5 | `current_step` | `VARCHAR(50)` | NOT NULL, default |
| 6 | `context` | `JSONB` | NOT NULL, default |
| 7 | `detected_language` | `VARCHAR(10)` | — |
| 8 | `last_message_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 9 | `expires_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 10 | `is_active` | `BOOLEAN` | NOT NULL, default |

### `docslot.departments`

TABLE D2: DEPARTMENTS (for hospitals).

_Source: `database/03_docslot.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `department_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `name` | `VARCHAR(100)` | NOT NULL |
| 4 | `code` | `VARCHAR(20)` | — |
| 5 | `description` | `TEXT` | — |
| 6 | `icon` | `VARCHAR(50)` | — |
| 7 | `display_order` | `SMALLINT` | NOT NULL, default |
| 8 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 9 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 10 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.doctor_schedules`

TABLE D4: DOCTOR_SCHEDULES (recurring weekly availability).

_Source: `database/03_docslot.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `schedule_id` | `UUID` | PK, default |
| 2 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 3 | `day_of_week` | `SMALLINT` | NOT NULL |
| 4 | `start_time` | `TIME` | NOT NULL |
| 5 | `end_time` | `TIME` | NOT NULL |
| 6 | `slot_duration_minutes` | `SMALLINT` | NOT NULL, default |
| 7 | `max_patients_per_slot` | `SMALLINT` | NOT NULL, default |
| 8 | `break_start_time` | `TIME` | — |
| 9 | `break_end_time` | `TIME` | — |
| 10 | `is_active` | `BOOLEAN` | NOT NULL, default |

### `docslot.doctors`

TABLE D3: DOCTORS (healthcare providers — also covers lab technicians).

_Source: `database/03_docslot.sql` · 31 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `doctor_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `user_id` | `UUID` | FK→platform.users |
| 4 | `full_name` | `VARCHAR(200)` | NOT NULL |
| 5 | `display_name` | `VARCHAR(200)` | — |
| 6 | `gender` | `VARCHAR(10)` | — |
| 7 | `profile_image_url` | `TEXT` | — |
| 8 | `department_id` | `UUID` | FK→docslot.departments |
| 9 | `role` | `VARCHAR(30)` | NOT NULL, default |
| 10 | `specialization` | `VARCHAR(100)` | — |
| 11 | `sub_specialization` | `VARCHAR(100)` | — |
| 12 | `qualifications` | `JSONB` | NOT NULL, default |
| 13 | `experience_years` | `SMALLINT` | — |
| 14 | `languages_spoken` | `VARCHAR(10)[]` | default |
| 15 | `biography` | `TEXT` | — |
| 16 | `consultation_fee` | `DECIMAL(10,2)` | — |
| 17 | `follow_up_fee` | `DECIMAL(10,2)` | — |
| 18 | `phone` | `VARCHAR(15)` | — |
| 19 | `email` | `CITEXT` | — |
| 20 | `nmc_registration_number` | `VARCHAR(50)` | — |
| 21 | `nmc_state_council` | `VARCHAR(50)` | — |
| 22 | `nmc_registration_year` | `INT` | — |
| 23 | `nmc_expires_at` | `DATE` | — |
| 24 | `hpr_id` | `VARCHAR(50)` | — |
| 25 | `nmc_verification_status` | `VARCHAR(20)` | default |
| 26 | `nmc_verified_at` | `TIMESTAMPTZ` | — |
| 27 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 28 | `is_accepting_new_patients` | `BOOLEAN` | NOT NULL, default |
| 29 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 30 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 31 | `deleted_at` | `TIMESTAMPTZ` | — |

### `docslot.drug_alerts`

TABLE D17: DRUG_ALERTS (allergies/interactions flagged at prescription time).

_Source: `database/03_docslot.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `alert_id` | `UUID` | PK, default |
| 2 | `prescription_id` | `UUID` | FK→docslot.prescriptions, NOT NULL |
| 3 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 4 | `alert_type` | `VARCHAR(30)` | NOT NULL |
| 5 | `severity` | `VARCHAR(20)` | NOT NULL |
| 6 | `medication_name` | `VARCHAR(200)` | NOT NULL |
| 7 | `conflicting_record_id` | `UUID` | — |
| 8 | `description` | `TEXT` | NOT NULL |
| 9 | `overridden` | `BOOLEAN` | NOT NULL, default |
| 10 | `overridden_by_user_id` | `UUID` | FK→platform.users |
| 11 | `override_reason` | `TEXT` | — |
| 12 | `overridden_at` | `TIMESTAMPTZ` | — |
| 13 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.family_members`

TABLE D9: FAMILY_MEMBERS (multi-patient under one phone).

_Source: `database/03_docslot.sql` · 6 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `family_member_id` | `UUID` | PK, default |
| 2 | `primary_patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 3 | `member_patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 4 | `relationship` | `VARCHAR(50)` | NOT NULL |
| 5 | `is_primary_decision_maker` | `BOOLEAN` | NOT NULL, default |
| 6 | `added_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.healthcare_facilities`

Rather than overloading platform.

_Source: `database/03_docslot.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `facility_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL, UNIQUE |
| 3 | `facility_type` | `VARCHAR(30)` | NOT NULL |
| 4 | `specialty_focus` | `VARCHAR(100)` | — |
| 5 | `whatsapp_business_phone_id` | `VARCHAR(50)` | — |
| 6 | `whatsapp_access_token` | `TEXT` | — |
| 7 | `whatsapp_verified_at` | `TIMESTAMPTZ` | — |
| 8 | `hfr_id` | `VARCHAR(50)` | — |
| 9 | `hfr_status` | `VARCHAR(20)` | default |
| 10 | `appointment_settings` | `JSONB` | NOT NULL, default |
| 11 | `business_hours` | `JSONB` | NOT NULL, default |
| 12 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 13 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.lab_reports`

_Source: `database/03_docslot.sql` · 20 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `report_id` | `UUID` | PK, default |
| 2 | `report_number` | `VARCHAR(30)` | NOT NULL, UNIQUE |
| 3 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL |
| 4 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 5 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 6 | `test_id` | `UUID` | FK→docslot.test_catalog |
| 7 | `file_url` | `TEXT` | — |
| 8 | `file_name` | `VARCHAR(200)` | — |
| 9 | `file_size_bytes` | `BIGINT` | — |
| 10 | `file_mime_type` | `VARCHAR(100)` | — |
| 11 | `structured_results` | `JSONB` | — |
| 12 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 13 | `uploaded_at` | `TIMESTAMPTZ` | — |
| 14 | `uploaded_by_user_id` | `UUID` | FK→platform.users |
| 15 | `delivered_at` | `TIMESTAMPTZ` | — |
| 16 | `delivery_message_id` | `VARCHAR(100)` | — |
| 17 | `has_critical_findings` | `BOOLEAN` | NOT NULL, default |
| 18 | `critical_findings_notified_at` | `TIMESTAMPTZ` | — |
| 19 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 20 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.opd_tokens`

TABLE D12: OPD_TOKENS (queue management for hospital OPD).

_Source: `database/03_docslot.sql` · 11 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `token_id` | `UUID` | PK, default |
| 2 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL, UNIQUE |
| 3 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 4 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 5 | `token_date` | `DATE` | NOT NULL |
| 6 | `token_number` | `INT` | NOT NULL |
| 7 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 8 | `called_at` | `TIMESTAMPTZ` | — |
| 9 | `consultation_started_at` | `TIMESTAMPTZ` | — |
| 10 | `completed_at` | `TIMESTAMPTZ` | — |
| 11 | `estimated_wait_minutes` | `INT` | — |

### `docslot.outbox_messages`

TABLE D21: OUTBOX_MESSAGES (reliable WhatsApp delivery queue).

_Source: `database/03_docslot.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `outbox_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `patient_id` | `UUID` | FK→docslot.patients |
| 4 | `message_intent` | `VARCHAR(50)` | NOT NULL |
| 5 | `payload` | `JSONB` | NOT NULL |
| 6 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 7 | `attempt_count` | `SMALLINT` | NOT NULL, default |
| 8 | `max_attempts` | `SMALLINT` | NOT NULL, default |
| 9 | `last_error` | `TEXT` | — |
| 10 | `next_retry_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 11 | `sent_at` | `TIMESTAMPTZ` | — |
| 12 | `whatsapp_message_id` | `VARCHAR(100)` | — |
| 13 | `correlation_id` | `VARCHAR(100)` | — |
| 14 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.patient_medical_history`

TABLE D15: PATIENT_MEDICAL_HISTORY (allergies, conditions, medications).

_Source: `database/03_docslot.sql` · 15 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `history_id` | `UUID` | PK, default |
| 2 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `record_type` | `VARCHAR(30)` | NOT NULL |
| 5 | `title` | `VARCHAR(200)` | NOT NULL |
| 6 | `description` | `TEXT` | — |
| 7 | `started_date` | `DATE` | — |
| 8 | `ended_date` | `DATE` | — |
| 9 | `severity` | `VARCHAR(20)` | — |
| 10 | `icd10_code` | `VARCHAR(10)` | — |
| 11 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 12 | `is_critical` | `BOOLEAN` | NOT NULL, default |
| 13 | `added_by_user_id` | `UUID` | FK→platform.users |
| 14 | `added_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 15 | `metadata` | `JSONB` | default |

### `docslot.patient_tenant_links`

Tracks the relationship between a patient and the healthcare facilities they've visited.

_Source: `database/03_docslot.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `link_id` | `UUID` | PK, default |
| 2 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `patient_local_id` | `VARCHAR(50)` | — |
| 5 | `first_visit_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 6 | `last_visit_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 7 | `total_visits` | `INT` | NOT NULL, default |
| 8 | `tenant_notes` | `TEXT` | — |

### `docslot.patients`

Patients exist at platform level (one phone = one patient identity).

_Source: `database/03_docslot.sql` · 30 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `patient_id` | `UUID` | PK, default |
| 2 | `phone_number` | `VARCHAR(15)` | NOT NULL, UNIQUE |
| 3 | `whatsapp_id` | `VARCHAR(50)` | — |
| 4 | `full_name` | `VARCHAR(200)` | — |
| 5 | `date_of_birth` | `DATE` | — |
| 6 | `age` | `SMALLINT` | — |
| 7 | `gender` | `VARCHAR(10)` | — |
| 8 | `blood_group` | `VARCHAR(5)` | — |
| 9 | `email` | `CITEXT` | — |
| 10 | `address_line1` | `VARCHAR(200)` | — |
| 11 | `city` | `VARCHAR(100)` | — |
| 12 | `state` | `VARCHAR(100)` | — |
| 13 | `pin_code` | `VARCHAR(10)` | — |
| 14 | `country` | `VARCHAR(2)` | NOT NULL, default |
| 15 | `emergency_contact_name` | `VARCHAR(200)` | — |
| 16 | `emergency_contact_phone` | `VARCHAR(15)` | — |
| 17 | `emergency_contact_relationship` | `VARCHAR(50)` | — |
| 18 | `aadhaar_last_4` | `VARCHAR(4)` | — |
| 19 | `preferred_language` | `VARCHAR(10)` | NOT NULL, default |
| 20 | `preferred_communication` | `VARCHAR(20)` | default |
| 21 | `consent_given_at` | `TIMESTAMPTZ` | — |
| 22 | `consent_version` | `VARCHAR(20)` | — |
| 23 | `consent_ip_address` | `INET` | — |
| 24 | `data_retention_until` | `TIMESTAMPTZ` | — |
| 25 | `deletion_requested_at` | `TIMESTAMPTZ` | — |
| 26 | `last_incoming_message_at` | `TIMESTAMPTZ` | — |
| 27 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 28 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 29 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 30 | `deleted_at` | `TIMESTAMPTZ` | — |

### `docslot.prescriptions`

_Source: `database/03_docslot.sql` · 20 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `prescription_id` | `UUID` | PK, default |
| 2 | `prescription_number` | `VARCHAR(30)` | NOT NULL, UNIQUE |
| 3 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL |
| 4 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 5 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 6 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 7 | `chief_complaints` | `TEXT` | — |
| 8 | `examination` | `TEXT` | — |
| 9 | `diagnosis` | `TEXT` | — |
| 10 | `medications` | `JSONB` | NOT NULL, default |
| 11 | `investigations` | `JSONB` | default |
| 12 | `advice` | `TEXT` | — |
| 13 | `follow_up_in_days` | `INT` | — |
| 14 | `pdf_url` | `TEXT` | — |
| 15 | `file_name` | `VARCHAR(200)` | — |
| 16 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 17 | `delivered_at` | `TIMESTAMPTZ` | — |
| 18 | `delivery_message_id` | `VARCHAR(100)` | — |
| 19 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 20 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.procedure_catalog`

TABLE D14: PROCEDURE_CATALOG (for hospitals — surgical/IPD procedures).

_Source: `database/03_docslot.sql` · 24 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `procedure_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `department_id` | `UUID` | FK→docslot.departments |
| 4 | `procedure_name` | `VARCHAR(200)` | NOT NULL |
| 5 | `procedure_code` | `VARCHAR(50)` | — |
| 6 | `category` | `VARCHAR(50)` | NOT NULL |
| 7 | `description` | `TEXT` | — |
| 8 | `typical_duration_hours` | `DECIMAL(5,2)` | — |
| 9 | `base_price` | `DECIMAL(10,2)` | NOT NULL |
| 10 | `doctor_fee` | `DECIMAL(10,2)` | NOT NULL, default |
| 11 | `anesthesia_fee` | `DECIMAL(10,2)` | NOT NULL, default |
| 12 | `consumables_estimate` | `DECIMAL(10,2)` | NOT NULL, default |
| 13 | `room_charges_per_day` | `DECIMAL(10,2)` | NOT NULL, default |
| 14 | `minimum_stay_days` | `SMALLINT` | NOT NULL, default |
| 15 | `estimated_min_total` | `DECIMAL(10,2)` | NOT NULL |
| 16 | `estimated_max_total` | `DECIMAL(10,2)` | NOT NULL |
| 17 | `inclusions` | `JSONB` | NOT NULL, default |
| 18 | `exclusions` | `JSONB` | NOT NULL, default |
| 19 | `cashless_eligible` | `BOOLEAN` | NOT NULL, default |
| 20 | `ab_pmjay_covered` | `BOOLEAN` | NOT NULL, default |
| 21 | `ab_pmjay_package_code` | `VARCHAR(20)` | — |
| 22 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 23 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 24 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.processed_messages`

TABLE D22: PROCESSED_MESSAGES (webhook idempotency).

_Source: `database/03_docslot.sql` · 2 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `whatsapp_message_id` | `VARCHAR(100)` | PK |
| 2 | `processed_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.reviews`

TABLE D24: REVIEWS (patient ratings of doctors).

_Source: `database/03_docslot.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `review_id` | `UUID` | PK, default |
| 2 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL, UNIQUE |
| 3 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 4 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 5 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 6 | `rating` | `SMALLINT` | NOT NULL |
| 7 | `comment` | `TEXT` | — |
| 8 | `aspects` | `JSONB` | default |
| 9 | `is_anonymous` | `BOOLEAN` | NOT NULL, default |
| 10 | `is_verified` | `BOOLEAN` | NOT NULL, default |
| 11 | `is_published` | `BOOLEAN` | NOT NULL, default |
| 12 | `response_from_doctor` | `TEXT` | — |
| 13 | `responded_at` | `TIMESTAMPTZ` | — |
| 14 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.schedule_overrides`

TABLE D5: SCHEDULE_OVERRIDES (holidays, leaves, special hours).

_Source: `database/03_docslot.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `override_id` | `UUID` | PK, default |
| 2 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 3 | `override_date` | `DATE` | NOT NULL |
| 4 | `is_blocked` | `BOOLEAN` | NOT NULL, default |
| 5 | `custom_start_time` | `TIME` | — |
| 6 | `custom_end_time` | `TIME` | — |
| 7 | `reason` | `VARCHAR(200)` | — |
| 8 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.test_catalog`

TABLE D13: TEST_CATALOG (for pathology labs).

_Source: `database/03_docslot.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `test_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `test_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `test_code` | `VARCHAR(50)` | — |
| 5 | `category` | `VARCHAR(50)` | — |
| 6 | `description` | `TEXT` | — |
| 7 | `sample_type` | `VARCHAR(50)` | — |
| 8 | `preparation_instructions` | `TEXT` | — |
| 9 | `price` | `DECIMAL(10,2)` | — |
| 10 | `discount_price` | `DECIMAL(10,2)` | — |
| 11 | `report_turnaround_hours` | `SMALLINT` | — |
| 12 | `is_home_collection_available` | `BOOLEAN` | NOT NULL, default |
| 13 | `home_collection_fee` | `DECIMAL(10,2)` | — |
| 14 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 15 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.time_slots`

TABLE D6: TIME_SLOTS (generated bookable slots).

_Source: `database/03_docslot.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `slot_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 4 | `slot_date` | `DATE` | NOT NULL |
| 5 | `start_time` | `TIME` | NOT NULL |
| 6 | `end_time` | `TIME` | NOT NULL |
| 7 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 8 | `current_count` | `SMALLINT` | NOT NULL, default |
| 9 | `max_count` | `SMALLINT` | NOT NULL, default |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.wa_contact_profiles`

One row per (tenant, WhatsApp number).

_Source: `database/09_chat_identity.sql` · 20 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `profile_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `phone` | `VARCHAR(15)` | NOT NULL |
| 4 | `display_name` | `VARCHAR(200)` | — |
| 5 | `default_booking_for` | `VARCHAR(10)` | NOT NULL, default |
| 6 | `last_relation` | `VARCHAR(20)` | — |
| 7 | `linked_patient_id` | `UUID` | FK→docslot.patients |
| 8 | `linked_broker_id` | `UUID` | FK→commission.brokers |
| 9 | `distinct_patients_90d` | `INT` | NOT NULL, default |
| 10 | `partner_nudge_sent_at` | `TIMESTAMPTZ` | — |
| 11 | `partner_nudge_count` | `INT` | NOT NULL, default |
| 12 | `app_installed` | `BOOLEAN` | NOT NULL, default |
| 13 | `app_install_nudge_count` | `INT` | NOT NULL, default |
| 14 | `last_app_nudge_at` | `TIMESTAMPTZ` | — |
| 15 | `history_sync_consent` | `BOOLEAN` | NOT NULL, default |
| 16 | `history_sync_consent_at` | `TIMESTAMPTZ` | — |
| 17 | `preferred_language` | `VARCHAR(5)` | NOT NULL, default |
| 18 | `last_seen_at` | `TIMESTAMPTZ` | — |
| 19 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 20 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `docslot.wa_message_log`

TABLE D20: WA_MESSAGE_LOG (WhatsApp message tracking).

_Source: `database/03_docslot.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `log_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `patient_id` | `UUID` | FK→docslot.patients |
| 4 | `conversation_id` | `UUID` | FK→docslot.conversations |
| 5 | `whatsapp_message_id` | `VARCHAR(100)` | — |
| 6 | `direction` | `VARCHAR(10)` | NOT NULL |
| 7 | `message_type` | `VARCHAR(20)` | NOT NULL |
| 8 | `template_name` | `VARCHAR(100)` | — |
| 9 | `content` | `JSONB` | — |
| 10 | `status` | `VARCHAR(20)` | — |
| 11 | `error_code` | `VARCHAR(50)` | — |
| 12 | `cost_usd` | `DECIMAL(10,4)` | — |
| 13 | `sent_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 14 | `delivered_at` | `TIMESTAMPTZ` | — |
| 15 | `read_at` | `TIMESTAMPTZ` | — |
| 16 | `failed_at` | `TIMESTAMPTZ` | — |

### `docslot.waitlist`

_Source: `database/03_docslot.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `waitlist_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `patient_id` | `UUID` | FK→docslot.patients, NOT NULL |
| 4 | `doctor_id` | `UUID` | FK→docslot.doctors, NOT NULL |
| 5 | `requested_date` | `DATE` | NOT NULL |
| 6 | `requested_time_range` | `VARCHAR(20)` | — |
| 7 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 8 | `notified_at` | `TIMESTAMPTZ` | — |
| 9 | `expires_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Schema: `ai`

> AI services: LangGraph, embeddings, predictions, OCR
> Source: database/06

### `ai.ai_agent_runs`

Each time a LangGraph workflow executes, we log the run for audit, debugging,.

_Source: `database/06_ai_services.sql` · 32 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `run_id` | `UUID` | PK, default |
| 2 | `workflow_id` | `UUID` | FK→ai.ai_workflows, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `triggered_by_user_id` | `UUID` | FK→platform.users |
| 5 | `triggered_by_event` | `VARCHAR(100)` | — |
| 6 | `related_resource_type` | `VARCHAR(50)` | — |
| 7 | `related_resource_id` | `UUID` | — |
| 8 | `correlation_id` | `VARCHAR(100)` | — |
| 9 | `input_data` | `JSONB` | NOT NULL |
| 10 | `input_token_count` | `INT` | — |
| 11 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 12 | `started_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 13 | `completed_at` | `TIMESTAMPTZ` | — |
| 14 | `duration_ms` | `INT` | — |
| 15 | `iterations_used` | `INT` | — |
| 16 | `output_data` | `JSONB` | — |
| 17 | `output_token_count` | `INT` | — |
| 18 | `confidence_score` | `DECIMAL(4,3)` | — |
| 19 | `total_cost_usd` | `DECIMAL(10,6)` | — |
| 20 | `models_used` | `JSONB` | — |
| 21 | `human_approval_required` | `BOOLEAN` | NOT NULL, default |
| 22 | `approved_at` | `TIMESTAMPTZ` | — |
| 23 | `approved_by_user_id` | `UUID` | FK→platform.users |
| 24 | `approval_notes` | `TEXT` | — |
| 25 | `rejected_at` | `TIMESTAMPTZ` | — |
| 26 | `rejected_by_user_id` | `UUID` | FK→platform.users |
| 27 | `rejection_reason` | `TEXT` | — |
| 28 | `error_code` | `VARCHAR(50)` | — |
| 29 | `error_message` | `TEXT` | — |
| 30 | `failed_at_node` | `VARCHAR(100)` | — |
| 31 | `patient_consent_id` | `UUID` | FK→docslot.abdm_consents |
| 32 | `data_classes_accessed` | `VARCHAR(50)[]` | — |

### `ai.ai_agent_steps`

For debugging and observability — each node in a LangGraph workflow logs here.

_Source: `database/06_ai_services.sql` · 20 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `step_id` | `UUID` | PK, default |
| 2 | `run_id` | `UUID` | FK→ai.ai_agent_runs, NOT NULL |
| 3 | `step_number` | `INT` | NOT NULL |
| 4 | `node_name` | `VARCHAR(100)` | NOT NULL |
| 5 | `step_type` | `VARCHAR(30)` | NOT NULL |
| 6 | `model_used` | `VARCHAR(100)` | — |
| 7 | `prompt` | `TEXT` | — |
| 8 | `response` | `TEXT` | — |
| 9 | `input_tokens` | `INT` | — |
| 10 | `output_tokens` | `INT` | — |
| 11 | `cost_usd` | `DECIMAL(10,6)` | — |
| 12 | `tool_name` | `VARCHAR(100)` | — |
| 13 | `tool_input` | `JSONB` | — |
| 14 | `tool_output` | `JSONB` | — |
| 15 | `started_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `completed_at` | `TIMESTAMPTZ` | — |
| 17 | `duration_ms` | `INT` | — |
| 18 | `success` | `BOOLEAN` | NOT NULL, default |
| 19 | `error_message` | `TEXT` | — |
| 20 | `state_snapshot` | `JSONB` | — |

### `ai.ai_document_extractions`

For lab reports, prescriptions (paper), insurance cards.

_Source: `database/06_ai_services.sql` · 23 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `extraction_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `source_type` | `VARCHAR(50)` | NOT NULL |
| 4 | `source_url` | `TEXT` | NOT NULL |
| 5 | `source_mime_type` | `VARCHAR(100)` | — |
| 6 | `source_size_bytes` | `BIGINT` | — |
| 7 | `related_booking_id` | `UUID` | FK→docslot.bookings |
| 8 | `related_patient_id` | `UUID` | FK→docslot.patients |
| 9 | `ocr_engine` | `VARCHAR(50)` | — |
| 10 | `raw_ocr_text` | `TEXT` | — |
| 11 | `extraction_model` | `VARCHAR(100)` | — |
| 12 | `extracted_data` | `JSONB` | — |
| 13 | `overall_confidence` | `DECIMAL(4,3)` | — |
| 14 | `requires_human_review` | `BOOLEAN` | NOT NULL, default |
| 15 | `reviewed_by_user_id` | `UUID` | FK→platform.users |
| 16 | `reviewed_at` | `TIMESTAMPTZ` | — |
| 17 | `review_status` | `VARCHAR(20)` | — |
| 18 | `corrected_data` | `JSONB` | — |
| 19 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 20 | `encryption_key_id` | `UUID` | FK→platform.encryption_keys |
| 21 | `cost_usd` | `DECIMAL(10,6)` | — |
| 22 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 23 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ai.ai_feedback`

Critical for medical AI: doctors need to flag wrong AI outputs.

_Source: `database/06_ai_services.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `feedback_id` | `UUID` | PK, default |
| 2 | `run_id` | `UUID` | FK→ai.ai_agent_runs, NOT NULL |
| 3 | `user_id` | `UUID` | FK→platform.users |
| 4 | `feedback_type` | `VARCHAR(30)` | NOT NULL |
| 5 | `severity` | `VARCHAR(20)` | — |
| 6 | `notes` | `TEXT` | — |
| 7 | `suggested_correct_output` | `JSONB` | — |
| 8 | `requires_immediate_action` | `BOOLEAN` | NOT NULL, default |
| 9 | `triggered_workflow_rollback` | `BOOLEAN` | NOT NULL, default |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ai.ai_knowledge_bases`

A knowledge base is a collection of documents indexed for retrieval.

_Source: `database/06_ai_services.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `kb_id` | `UUID` | PK, default |
| 2 | `kb_key` | `VARCHAR(100)` | NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `name` | `VARCHAR(200)` | NOT NULL |
| 5 | `description` | `TEXT` | — |
| 6 | `source_type` | `VARCHAR(50)` | NOT NULL |
| 7 | `embedding_model` | `VARCHAR(100)` | NOT NULL |
| 8 | `document_count` | `INT` | NOT NULL, default |
| 9 | `last_indexed_at` | `TIMESTAMPTZ` | — |
| 10 | `requires_consent` | `BOOLEAN` | NOT NULL, default |
| 11 | `permission_required` | `VARCHAR(150)` | — |
| 12 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 13 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ai.ai_model_configs`

Tenants may have data sovereignty requirements: some hospitals require.

_Source: `database/06_ai_services.sql` · 22 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `config_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants |
| 3 | `use_case` | `VARCHAR(50)` | NOT NULL |
| 4 | `provider` | `VARCHAR(30)` | NOT NULL |
| 5 | `model_name` | `VARCHAR(100)` | NOT NULL |
| 6 | `endpoint_url` | `VARCHAR(500)` | — |
| 7 | `credential_reference` | `VARCHAR(500)` | — |
| 8 | `max_tokens` | `INT` | default |
| 9 | `temperature` | `DECIMAL(3,2)` | default |
| 10 | `system_prompt_template` | `TEXT` | — |
| 11 | `cost_per_1k_input_tokens` | `DECIMAL(10,6)` | — |
| 12 | `cost_per_1k_output_tokens` | `DECIMAL(10,6)` | — |
| 13 | `data_residency_region` | `VARCHAR(10)` | — |
| 14 | `bao_signed` | `BOOLEAN` | NOT NULL, default |
| 15 | `allows_phi` | `BOOLEAN` | NOT NULL, default |
| 16 | `requires_consent` | `BOOLEAN` | NOT NULL, default |
| 17 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 18 | `is_default_for_use_case` | `BOOLEAN` | NOT NULL, default |
| 19 | `created_by_user_id` | `UUID` | FK→platform.users |
| 20 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 21 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 22 | `metadata` | `JSONB` | NOT NULL, default |

### `ai.ai_predictions`

For non-LLM ML models: scikit-learn classifiers, XGBoost, etc.

_Source: `database/06_ai_services.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `prediction_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `model_name` | `VARCHAR(100)` | NOT NULL |
| 4 | `model_version` | `VARCHAR(50)` | NOT NULL |
| 5 | `prediction_type` | `VARCHAR(50)` | NOT NULL |
| 6 | `related_resource_type` | `VARCHAR(50)` | — |
| 7 | `related_resource_id` | `UUID` | — |
| 8 | `predicted_value` | `DECIMAL(10,4)` | NOT NULL |
| 9 | `confidence_interval` | `JSONB` | — |
| 10 | `features_used` | `JSONB` | — |
| 11 | `actual_outcome` | `DECIMAL(10,4)` | — |
| 12 | `outcome_observed_at` | `TIMESTAMPTZ` | — |
| 13 | `valid_until` | `TIMESTAMPTZ` | — |
| 14 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ai.ai_prompts`

Centralized prompt management.

_Source: `database/06_ai_services.sql` · 19 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `prompt_id` | `UUID` | PK, default |
| 2 | `prompt_key` | `VARCHAR(100)` | NOT NULL |
| 3 | `version` | `INT` | NOT NULL, default |
| 4 | `tenant_id` | `UUID` | FK→platform.tenants |
| 5 | `system_prompt` | `TEXT` | NOT NULL |
| 6 | `user_prompt_template` | `TEXT` | — |
| 7 | `expected_variables` | `VARCHAR(100)[]` | — |
| 8 | `use_case` | `VARCHAR(50)` | NOT NULL |
| 9 | `description` | `TEXT` | — |
| 10 | `tested_on_models` | `VARCHAR(100)[]` | — |
| 11 | `pii_handling_notes` | `TEXT` | — |
| 12 | `medical_safety_review_required` | `BOOLEAN` | NOT NULL, default |
| 13 | `medical_review_by_user_id` | `UUID` | FK→platform.users |
| 14 | `medical_review_at` | `TIMESTAMPTZ` | — |
| 15 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 16 | `activated_at` | `TIMESTAMPTZ` | — |
| 17 | `deprecated_at` | `TIMESTAMPTZ` | — |
| 18 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 19 | `created_by_user_id` | `UUID` | FK→platform.users |

### `ai.ai_workflows`

A LangGraph workflow is a stateful directed graph of LLM/tool calls.

_Source: `database/06_ai_services.sql` · 19 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `workflow_id` | `UUID` | PK, default |
| 2 | `workflow_key` | `VARCHAR(100)` | NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `name` | `VARCHAR(200)` | NOT NULL |
| 5 | `description` | `TEXT` | — |
| 6 | `version` | `INT` | NOT NULL, default |
| 7 | `use_case` | `VARCHAR(50)` | NOT NULL |
| 8 | `graph_definition` | `JSONB` | NOT NULL |
| 9 | `initial_state_schema` | `JSONB` | — |
| 10 | `output_schema` | `JSONB` | — |
| 11 | `timeout_seconds` | `INT` | NOT NULL, default |
| 12 | `max_iterations` | `INT` | NOT NULL, default |
| 13 | `requires_human_approval` | `BOOLEAN` | NOT NULL, default |
| 14 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 15 | `activated_at` | `TIMESTAMPTZ` | — |
| 16 | `activated_by_user_id` | `UUID` | FK→platform.users |
| 17 | `deprecated_at` | `TIMESTAMPTZ` | — |
| 18 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 19 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ai.embeddings`

Stores document embeddings for retrieval-augmented generation.

_Source: `database/06_ai_services.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `embedding_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `source_type` | `VARCHAR(50)` | NOT NULL |
| 4 | `source_id` | `UUID` | NOT NULL |
| 5 | `chunk_index` | `INT` | NOT NULL, default |
| 6 | `chunk_text` | `TEXT` | — |
| 7 | `chunk_text_hash` | `VARCHAR(64)` | — |
| 8 | `embedding_model` | `VARCHAR(100)` | NOT NULL |
| 9 | `embedding_dimensions` | `INT` | NOT NULL |
| 10 | `embedding_vector` | `BYTEA` | — |
| 11 | `metadata` | `JSONB` | NOT NULL, default |
| 12 | `patient_id` | `UUID` | FK→docslot.patients |
| 13 | `encryption_key_id` | `UUID` | FK→platform.encryption_keys |
| 14 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 15 | `expires_at` | `TIMESTAMPTZ` | — |
| 16 | `deleted_at` | `TIMESTAMPTZ` | — |

---

## Schema: `commission`

> Broker referral economy: brokers, attribution, rules, payouts (TDS/GST), disputes. PCPNDT CHECK-enforced
> Source: database/07

### `commission.attribution_disputes`

TABLE C9: ATTRIBUTION_DISPUTES (when tenant or broker disagrees).

_Source: `database/07_commission_broker.sql` · 16 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `dispute_id` | `UUID` | PK, default |
| 2 | `attribution_id` | `UUID` | FK→commission.attributions, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `raised_by` | `VARCHAR(20)` | NOT NULL |
| 5 | `raised_by_user_id` | `UUID` | FK→platform.users |
| 6 | `raised_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 7 | `dispute_reason` | `VARCHAR(50)` | NOT NULL |
| 8 | `description` | `TEXT` | NOT NULL |
| 9 | `evidence_urls` | `TEXT[]` | — |
| 10 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 11 | `resolved_at` | `TIMESTAMPTZ` | — |
| 12 | `resolved_by_user_id` | `UUID` | FK→platform.users |
| 13 | `resolution_notes` | `TEXT` | — |
| 14 | `resolution_amount_adjustment_inr` | `DECIMAL(10,2)` | — |
| 15 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 16 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.attributions`

One row per (broker, booking) attribution.

_Source: `database/07_commission_broker.sql` · 24 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `attribution_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `booking_id` | `UUID` | FK→docslot.bookings, NOT NULL |
| 4 | `broker_id` | `UUID` | FK→commission.brokers, NOT NULL |
| 5 | `attribution_source` | `VARCHAR(30)` | NOT NULL |
| 6 | `verification_status` | `VARCHAR(20)` | NOT NULL, default |
| 7 | `verified_at` | `TIMESTAMPTZ` | — |
| 8 | `patient_confirmation_message_id` | `UUID` | FK→docslot.wa_message_log |
| 9 | `admin_override_by_user_id` | `UUID` | FK→platform.users |
| 10 | `admin_override_reason` | `TEXT` | — |
| 11 | `referral_link_id` | `UUID` | FK→commission.referral_links |
| 12 | `referral_click_id` | `UUID` | FK→commission.referral_clicks |
| 13 | `source_metadata` | `JSONB` | NOT NULL, default |
| 14 | `rule_id` | `UUID` | FK→commission.commission_rules |
| 15 | `commission_amount_inr` | `DECIMAL(10,2)` | — |
| 16 | `commission_status` | `VARCHAR(20)` | NOT NULL, default |
| 17 | `attributed_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 18 | `earned_at` | `TIMESTAMPTZ` | — |
| 19 | `paid_at` | `TIMESTAMPTZ` | — |
| 20 | `payout_id` | `UUID` | FK→commission.payouts |
| 21 | `fraud_score` | `DECIMAL(4,3)` | — |
| 22 | `fraud_flags` | `VARCHAR(50)[]` | — |
| 23 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 24 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.broker_campaigns`

Tenants run periodic campaigns: "20% extra commission on cardiac surgery.

_Source: `database/07_commission_broker.sql` · 19 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `campaign_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `campaign_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `description` | `TEXT` | — |
| 5 | `target_broker_types` | `VARCHAR(30)[]` | — |
| 6 | `target_broker_tiers` | `VARCHAR(20)[]` | — |
| 7 | `target_services` | `VARCHAR(50)[]` | — |
| 8 | `target_doctor_ids` | `UUID[]` | — |
| 9 | `bonus_type` | `VARCHAR(20)` | NOT NULL |
| 10 | `bonus_value` | `DECIMAL(10,2)` | — |
| 11 | `min_bookings_for_bonus` | `INT` | — |
| 12 | `starts_at` | `TIMESTAMPTZ` | NOT NULL |
| 13 | `ends_at` | `TIMESTAMPTZ` | NOT NULL |
| 14 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 15 | `total_budget_inr` | `DECIMAL(12,2)` | — |
| 16 | `spent_so_far_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 17 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 18 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 19 | `created_by_user_id` | `UUID` | FK→platform.users |

### `commission.broker_tenant_links`

A broker can work with multiple tenants; commission rules are tenant-specific.

_Source: `database/07_commission_broker.sql` · 17 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `link_id` | `UUID` | PK, default |
| 2 | `broker_id` | `UUID` | FK→commission.brokers, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 5 | `activated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 6 | `activated_by_user_id` | `UUID` | FK→platform.users |
| 7 | `suspended_at` | `TIMESTAMPTZ` | — |
| 8 | `suspended_reason` | `TEXT` | — |
| 9 | `tier_override` | `VARCHAR(20)` | — |
| 10 | `contract_signed_at` | `TIMESTAMPTZ` | — |
| 11 | `contract_document_url` | `TEXT` | — |
| 12 | `bilateral_terms` | `JSONB` | NOT NULL, default |
| 13 | `total_attributions` | `INT` | NOT NULL, default |
| 14 | `total_earned_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 15 | `last_attribution_at` | `TIMESTAMPTZ` | — |
| 16 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 17 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.broker_wallets`

One row per broker.

_Source: `database/07_commission_broker.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `broker_id` | `UUID` | PK, FK→commission.brokers |
| 2 | `pending_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 3 | `earned_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 4 | `ready_to_pay_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 5 | `lifetime_attributions` | `INT` | NOT NULL, default |
| 6 | `lifetime_paid_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 7 | `lifetime_reversed_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 8 | `current_month_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 9 | `current_month_attributions` | `INT` | NOT NULL, default |
| 10 | `last_attribution_at` | `TIMESTAMPTZ` | — |
| 11 | `last_payout_at` | `TIMESTAMPTZ` | — |
| 12 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.brokers`

A broker is anyone authorized to refer patients to DocSlot facilities:.

_Source: `database/07_commission_broker.sql` · 35 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `broker_id` | `UUID` | PK, default |
| 2 | `phone` | `VARCHAR(15)` | NOT NULL, UNIQUE |
| 3 | `full_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `email` | `CITEXT` | — |
| 5 | `user_id` | `UUID` | FK→platform.users |
| 6 | `pan_number` | `VARCHAR(10)` | — |
| 7 | `pan_verified` | `BOOLEAN` | NOT NULL, default |
| 8 | `pan_verified_at` | `TIMESTAMPTZ` | — |
| 9 | `aadhaar_last_4` | `VARCHAR(4)` | — |
| 10 | `aadhaar_verified` | `BOOLEAN` | NOT NULL, default |
| 11 | `gst_number` | `VARCHAR(15)` | — |
| 12 | `gst_verified` | `BOOLEAN` | NOT NULL, default |
| 13 | `broker_type` | `VARCHAR(30)` | NOT NULL |
| 14 | `tier_level` | `VARCHAR(20)` | NOT NULL, default |
| 15 | `tier_upgraded_at` | `TIMESTAMPTZ` | — |
| 16 | `monthly_volume_inr` | `DECIMAL(12,2)` | default |
| 17 | `upi_id` | `VARCHAR(100)` | — |
| 18 | `bank_account_last_4` | `VARCHAR(4)` | — |
| 19 | `bank_ifsc` | `VARCHAR(11)` | — |
| 20 | `payout_method` | `VARCHAR(20)` | NOT NULL, default |
| 21 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 22 | `activated_at` | `TIMESTAMPTZ` | — |
| 23 | `activated_by_user_id` | `UUID` | FK→platform.users |
| 24 | `suspended_at` | `TIMESTAMPTZ` | — |
| 25 | `suspended_reason` | `TEXT` | — |
| 26 | `blacklisted_at` | `TIMESTAMPTZ` | — |
| 27 | `blacklist_reason` | `TEXT` | — |
| 28 | `referred_by_broker_id` | `UUID` | FK→commission.brokers |
| 29 | `onboarded_via` | `VARCHAR(50)` | — |
| 30 | `can_refer_pndt` | `BOOLEAN` | NOT NULL, default |
| 31 | `requires_consent_for_phi` | `BOOLEAN` | NOT NULL, default |
| 32 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 33 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 34 | `last_active_at` | `TIMESTAMPTZ` | — |
| 35 | `metadata` | `JSONB` | NOT NULL, default |

### `commission.commission_rules`

Each tenant configures their own commission structure.

_Source: `database/07_commission_broker.sql` · 27 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `rule_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `rule_name` | `VARCHAR(200)` | NOT NULL |
| 4 | `rule_key` | `VARCHAR(50)` | NOT NULL |
| 5 | `description` | `TEXT` | — |
| 6 | `applies_to_broker_tier` | `VARCHAR(20)[]` | — |
| 7 | `applies_to_broker_type` | `VARCHAR(30)[]` | — |
| 8 | `applies_to_service_type` | `VARCHAR(50)[]` | — |
| 9 | `applies_to_department_id` | `UUID` | FK→docslot.departments |
| 10 | `applies_to_doctor_id` | `UUID` | FK→docslot.doctors |
| 11 | `min_booking_value_inr` | `DECIMAL(12,2)` | — |
| 12 | `max_booking_value_inr` | `DECIMAL(12,2)` | — |
| 13 | `calc_type` | `VARCHAR(20)` | NOT NULL |
| 14 | `flat_amount_inr` | `DECIMAL(10,2)` | — |
| 15 | `percentage` | `DECIMAL(5,2)` | — |
| 16 | `tiered_table` | `JSONB` | — |
| 17 | `min_commission_inr` | `DECIMAL(10,2)` | — |
| 18 | `max_commission_inr` | `DECIMAL(10,2)` | — |
| 19 | `max_monthly_per_broker_inr` | `DECIMAL(12,2)` | — |
| 20 | `priority` | `INT` | NOT NULL, default |
| 21 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 22 | `effective_from` | `TIMESTAMPTZ` | NOT NULL, default |
| 23 | `effective_until` | `TIMESTAMPTZ` | — |
| 24 | `approved_by_user_id` | `UUID` | FK→platform.users |
| 25 | `approved_at` | `TIMESTAMPTZ` | — |
| 26 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 27 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.payouts`

A payout aggregates many attributions for one broker for one settlement.

_Source: `database/07_commission_broker.sql` · 28 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `payout_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `broker_id` | `UUID` | FK→commission.brokers, NOT NULL |
| 4 | `period_start` | `DATE` | NOT NULL |
| 5 | `period_end` | `DATE` | NOT NULL |
| 6 | `attribution_count` | `INT` | NOT NULL, default |
| 7 | `gross_amount_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 8 | `tds_rate` | `DECIMAL(5,2)` | NOT NULL, default |
| 9 | `tds_amount_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 10 | `gst_rate` | `DECIMAL(5,2)` | — |
| 11 | `gst_amount_inr` | `DECIMAL(12,2)` | default |
| 12 | `net_amount_inr` | `DECIMAL(12,2)` | NOT NULL, default |
| 13 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 14 | `payment_method` | `VARCHAR(20)` | NOT NULL |
| 15 | `payment_reference` | `VARCHAR(100)` | — |
| 16 | `payment_gateway` | `VARCHAR(50)` | — |
| 17 | `invoice_number` | `VARCHAR(50)` | UNIQUE |
| 18 | `invoice_pdf_url` | `TEXT` | — |
| 19 | `invoice_generated_at` | `TIMESTAMPTZ` | — |
| 20 | `form_16a_url` | `TEXT` | — |
| 21 | `approved_by_user_id` | `UUID` | FK→platform.users |
| 22 | `approved_at` | `TIMESTAMPTZ` | — |
| 23 | `initiated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 24 | `completed_at` | `TIMESTAMPTZ` | — |
| 25 | `failure_reason` | `TEXT` | — |
| 26 | `metadata` | `JSONB` | NOT NULL, default |
| 27 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 28 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `commission.referral_clicks`

Every click on a referral link logged.

_Source: `database/07_commission_broker.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `click_id` | `UUID` | PK, default |
| 2 | `link_id` | `UUID` | FK→commission.referral_links, NOT NULL |
| 3 | `short_code` | `VARCHAR(20)` | NOT NULL |
| 4 | `clicked_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 5 | `session_token` | `VARCHAR(64)` | — |
| 6 | `ip_address_hash` | `VARCHAR(64)` | — |
| 7 | `user_agent_brief` | `VARCHAR(50)` | — |
| 8 | `referrer_source` | `VARCHAR(30)` | — |
| 9 | `converted_to_booking_id` | `UUID` | FK→docslot.bookings |
| 10 | `converted_at` | `TIMESTAMPTZ` | — |

### `commission.referral_links`

Each broker can generate unique referral links for different campaigns,.

_Source: `database/07_commission_broker.sql` · 17 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `link_id` | `UUID` | PK, default |
| 2 | `broker_id` | `UUID` | FK→commission.brokers, NOT NULL |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants |
| 4 | `short_code` | `VARCHAR(20)` | NOT NULL, UNIQUE |
| 5 | `target_url` | `TEXT` | default |
| 6 | `target_facility_id` | `UUID` | FK→docslot.healthcare_facilities |
| 7 | `target_doctor_id` | `UUID` | FK→docslot.doctors |
| 8 | `target_department_id` | `UUID` | FK→docslot.departments |
| 9 | `target_service_type` | `VARCHAR(50)` | — |
| 10 | `campaign_name` | `VARCHAR(200)` | — |
| 11 | `notes` | `TEXT` | — |
| 12 | `expires_at` | `TIMESTAMPTZ` | — |
| 13 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 14 | `click_count` | `INT` | NOT NULL, default |
| 15 | `conversion_count` | `INT` | NOT NULL, default |
| 16 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 17 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Schema: `ruralreach`

> RuralReach (future): mobile diagnostics logistics
> Source: database/04

### `ruralreach.cold_chain_logs`

Cold chain temperature logs.

_Source: `database/04_future_products.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `log_id` | `UUID` | PK, default |
| 2 | `vehicle_id` | `UUID` | FK→ruralreach.vehicles, NOT NULL |
| 3 | `request_id` | `UUID` | FK→ruralreach.collection_requests |
| 4 | `temperature_celsius` | `DECIMAL(4,1)` | NOT NULL |
| 5 | `humidity_percent` | `SMALLINT` | — |
| 6 | `is_within_range` | `BOOLEAN` | NOT NULL |
| 7 | `recorded_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ruralreach.collection_requests`

Collection requests (links to DocSlot bookings).

_Source: `database/04_future_products.sql` · 28 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `request_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `source_booking_id` | `UUID` | — |
| 4 | `patient_id` | `UUID` | — |
| 5 | `patient_phone` | `VARCHAR(15)` | NOT NULL |
| 6 | `patient_name` | `VARCHAR(200)` | NOT NULL |
| 7 | `tests_requested` | `JSONB` | NOT NULL |
| 8 | `collection_address` | `TEXT` | NOT NULL |
| 9 | `pickup_lat` | `DECIMAL(10,7)` | — |
| 10 | `pickup_lng` | `DECIMAL(10,7)` | — |
| 11 | `pin_code` | `VARCHAR(10)` | NOT NULL |
| 12 | `zone_id` | `UUID` | FK→ruralreach.service_zones |
| 13 | `requested_window_start` | `TIMESTAMPTZ` | NOT NULL |
| 14 | `requested_window_end` | `TIMESTAMPTZ` | NOT NULL |
| 15 | `assigned_vehicle_id` | `UUID` | FK→ruralreach.vehicles |
| 16 | `assigned_technician_id` | `UUID` | FK→ruralreach.technicians |
| 17 | `estimated_arrival_at` | `TIMESTAMPTZ` | — |
| 18 | `status` | `VARCHAR(30)` | NOT NULL, default |
| 19 | `technician_arrived_at` | `TIMESTAMPTZ` | — |
| 20 | `collected_at` | `TIMESTAMPTZ` | — |
| 21 | `delivered_to_lab_at` | `TIMESTAMPTZ` | — |
| 22 | `target_lab_tenant_id` | `UUID` | FK→platform.tenants |
| 23 | `base_fee` | `DECIMAL(10,2)` | — |
| 24 | `zone_fee` | `DECIMAL(10,2)` | — |
| 25 | `total_amount` | `DECIMAL(10,2)` | — |
| 26 | `notes` | `TEXT` | — |
| 27 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 28 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ruralreach.route_stops`

_Source: `database/04_future_products.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `stop_id` | `UUID` | PK, default |
| 2 | `route_id` | `UUID` | FK→ruralreach.routes, NOT NULL |
| 3 | `request_id` | `UUID` | FK→ruralreach.collection_requests, NOT NULL |
| 4 | `sequence_number` | `SMALLINT` | NOT NULL |
| 5 | `planned_arrival_at` | `TIMESTAMPTZ` | — |
| 6 | `actual_arrival_at` | `TIMESTAMPTZ` | — |
| 7 | `actual_departure_at` | `TIMESTAMPTZ` | — |

### `ruralreach.routes`

Routes (optimized daily routes for vehicles).

_Source: `database/04_future_products.sql` · 13 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `route_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `vehicle_id` | `UUID` | FK→ruralreach.vehicles, NOT NULL |
| 4 | `technician_id` | `UUID` | FK→ruralreach.technicians, NOT NULL |
| 5 | `route_date` | `DATE` | NOT NULL |
| 6 | `planned_start_at` | `TIMESTAMPTZ` | — |
| 7 | `actual_start_at` | `TIMESTAMPTZ` | — |
| 8 | `actual_end_at` | `TIMESTAMPTZ` | — |
| 9 | `total_stops` | `INT` | NOT NULL, default |
| 10 | `completed_stops` | `INT` | NOT NULL, default |
| 11 | `total_distance_km` | `DECIMAL(8,2)` | — |
| 12 | `optimized_path` | `JSONB` | — |
| 13 | `status` | `VARCHAR(20)` | NOT NULL, default |

### `ruralreach.sample_chain_of_custody`

Sample chain of custody.

_Source: `database/04_future_products.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `custody_id` | `UUID` | PK, default |
| 2 | `request_id` | `UUID` | FK→ruralreach.collection_requests, NOT NULL |
| 3 | `sample_barcode` | `VARCHAR(50)` | NOT NULL, UNIQUE |
| 4 | `sample_type` | `VARCHAR(50)` | — |
| 5 | `collected_at` | `TIMESTAMPTZ` | NOT NULL |
| 6 | `collected_by_technician_id` | `UUID` | FK→ruralreach.technicians |
| 7 | `handover_to_vehicle_id` | `UUID` | FK→ruralreach.vehicles |
| 8 | `handover_at` | `TIMESTAMPTZ` | — |
| 9 | `delivered_to_lab_at` | `TIMESTAMPTZ` | — |
| 10 | `received_by_user_id` | `UUID` | FK→platform.users |
| 11 | `rejected` | `BOOLEAN` | NOT NULL, default |
| 12 | `rejection_reason` | `TEXT` | — |

### `ruralreach.service_zones`

Service zones.

_Source: `database/04_future_products.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `zone_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `zone_name` | `VARCHAR(100)` | NOT NULL |
| 4 | `pin_codes` | `VARCHAR(10)[]` | — |
| 5 | `boundary_polygon` | `JSONB` | — |
| 6 | `service_fee` | `DECIMAL(10,2)` | — |
| 7 | `estimated_arrival_minutes` | `INT` | — |
| 8 | `is_active` | `BOOLEAN` | NOT NULL, default |

### `ruralreach.technicians`

Field technicians.

_Source: `database/04_future_products.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `technician_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `user_id` | `UUID` | FK→platform.users |
| 4 | `full_name` | `VARCHAR(200)` | NOT NULL |
| 5 | `gender` | `VARCHAR(10)` | — |
| 6 | `phone` | `VARCHAR(15)` | NOT NULL |
| 7 | `employee_id` | `VARCHAR(50)` | — |
| 8 | `certifications` | `JSONB` | default |
| 9 | `specialized_tests` | `VARCHAR(50)[]` | — |
| 10 | `current_lat` | `DECIMAL(10,7)` | — |
| 11 | `current_lng` | `DECIMAL(10,7)` | — |
| 12 | `is_available` | `BOOLEAN` | NOT NULL, default |
| 13 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 14 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `ruralreach.vehicles`

Vehicle fleet.

_Source: `database/04_future_products.sql` · 18 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `vehicle_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `vehicle_number` | `VARCHAR(20)` | NOT NULL, UNIQUE |
| 4 | `vehicle_type` | `VARCHAR(30)` | NOT NULL |
| 5 | `capacity_samples` | `INT` | NOT NULL, default |
| 6 | `has_refrigeration` | `BOOLEAN` | NOT NULL, default |
| 7 | `min_temp_celsius` | `DECIMAL(4,1)` | — |
| 8 | `max_temp_celsius` | `DECIMAL(4,1)` | — |
| 9 | `current_status` | `VARCHAR(20)` | NOT NULL, default |
| 10 | `current_lat` | `DECIMAL(10,7)` | — |
| 11 | `current_lng` | `DECIMAL(10,7)` | — |
| 12 | `last_gps_update` | `TIMESTAMPTZ` | — |
| 13 | `fuel_type` | `VARCHAR(20)` | — |
| 14 | `registration_expires` | `DATE` | — |
| 15 | `insurance_expires` | `DATE` | — |
| 16 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 17 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 18 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Schema: `safeher`

> SafeHer (future): women-only healthcare
> Source: database/04

### `safeher.anonymous_bookings`

Anonymous bookings (for sensitive procedures — partner shouldn't know).

_Source: `database/04_future_products.sql` · 9 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `anon_booking_id` | `UUID` | PK, default |
| 2 | `anonymous_code` | `VARCHAR(20)` | NOT NULL, UNIQUE |
| 3 | `related_booking_id` | `UUID` | — |
| 4 | `secondary_phone` | `VARCHAR(15)` | NOT NULL |
| 5 | `contact_window_start` | `TIME` | — |
| 6 | `contact_window_end` | `TIME` | — |
| 7 | `contact_method` | `VARCHAR(20)` | default |
| 8 | `expires_at` | `TIMESTAMPTZ` | — |
| 9 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `safeher.chaperone_bookings`

Chaperone bookings.

_Source: `database/04_future_products.sql` · 19 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `chaperone_booking_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `related_booking_id` | `UUID` | — |
| 4 | `patient_phone` | `VARCHAR(15)` | NOT NULL |
| 5 | `patient_name` | `VARCHAR(200)` | — |
| 6 | `chaperone_staff_id` | `UUID` | FK→safeher.female_staff, NOT NULL |
| 7 | `pickup_address` | `TEXT` | NOT NULL |
| 8 | `destination_address` | `TEXT` | NOT NULL |
| 9 | `scheduled_pickup_at` | `TIMESTAMPTZ` | NOT NULL |
| 10 | `estimated_duration_hours` | `DECIMAL(4,2)` | — |
| 11 | `actual_pickup_at` | `TIMESTAMPTZ` | — |
| 12 | `actual_return_at` | `TIMESTAMPTZ` | — |
| 13 | `hourly_rate` | `DECIMAL(10,2)` | — |
| 14 | `total_amount` | `DECIMAL(10,2)` | — |
| 15 | `status` | `VARCHAR(20)` | NOT NULL, default |
| 16 | `is_anonymous_booking` | `BOOLEAN` | NOT NULL, default |
| 17 | `notes` | `TEXT` | — |
| 18 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 19 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `safeher.cultural_preferences`

Cultural preferences (regional context).

_Source: `database/04_future_products.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `preference_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `region` | `VARCHAR(50)` | — |
| 4 | `supported_languages` | `VARCHAR(10)[]` | — |
| 5 | `respects_purdah` | `BOOLEAN` | NOT NULL, default |
| 6 | `veil_friendly_examination` | `BOOLEAN` | NOT NULL, default |
| 7 | `family_decision_protocol` | `VARCHAR(50)` | — |
| 8 | `metadata` | `JSONB` | default |

### `safeher.female_friendly_reviews`

Reviews specifically about female-friendliness.

_Source: `database/04_future_products.sql` · 12 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `review_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `related_booking_id` | `UUID` | — |
| 4 | `patient_id` | `UUID` | — |
| 5 | `privacy_rating` | `SMALLINT` | — |
| 6 | `staff_sensitivity_rating` | `SMALLINT` | — |
| 7 | `facility_rating` | `SMALLINT` | — |
| 8 | `chaperone_rating` | `SMALLINT` | — |
| 9 | `comment` | `TEXT` | — |
| 10 | `is_published` | `BOOLEAN` | NOT NULL, default |
| 11 | `is_anonymous` | `BOOLEAN` | NOT NULL, default |
| 12 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `safeher.female_only_facilities`

Women's-only zones (clinics, waiting areas).

_Source: `database/04_future_products.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `facility_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL, UNIQUE |
| 3 | `has_separate_entrance` | `BOOLEAN` | NOT NULL, default |
| 4 | `has_female_only_waiting_area` | `BOOLEAN` | NOT NULL, default |
| 5 | `has_female_only_restrooms` | `BOOLEAN` | NOT NULL, default |
| 6 | `has_curtained_examination` | `BOOLEAN` | NOT NULL, default |
| 7 | `all_female_staff` | `BOOLEAN` | NOT NULL, default |
| 8 | `certifications` | `JSONB` | default |
| 9 | `photo_urls` | `TEXT[]` | — |
| 10 | `verified_at` | `TIMESTAMPTZ` | — |

### `safeher.female_only_slots`

Female-only slots (overlay on docslot.

_Source: `database/04_future_products.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `slot_designation_id` | `UUID` | PK, default |
| 2 | `slot_id` | `UUID` | FK→docslot.time_slots, NOT NULL, UNIQUE |
| 3 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 4 | `designation_reason` | `VARCHAR(100)` | — |
| 5 | `requires_female_doctor` | `BOOLEAN` | NOT NULL, default |
| 6 | `requires_female_technician` | `BOOLEAN` | NOT NULL, default |
| 7 | `designated_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 8 | `designated_by_user_id` | `UUID` | FK→platform.users |

### `safeher.female_staff`

Female staff registry (extends docslot.

_Source: `database/04_future_products.sql` · 15 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `staff_id` | `UUID` | PK, default |
| 2 | `tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `doctor_id` | `UUID` | FK→docslot.doctors |
| 4 | `technician_id` | `UUID` | FK→ruralreach.technicians |
| 5 | `full_name` | `VARCHAR(200)` | NOT NULL |
| 6 | `languages_spoken` | `VARCHAR(10)[]` | NOT NULL, default |
| 7 | `specializations` | `VARCHAR(100)[]` | — |
| 8 | `background_verified` | `BOOLEAN` | NOT NULL, default |
| 9 | `background_verified_at` | `TIMESTAMPTZ` | — |
| 10 | `photo_url` | `TEXT` | — |
| 11 | `bio` | `TEXT` | — |
| 12 | `is_available_for_chaperone` | `BOOLEAN` | NOT NULL, default |
| 13 | `chaperone_hourly_rate` | `DECIMAL(10,2)` | — |
| 14 | `is_active` | `BOOLEAN` | NOT NULL, default |
| 15 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `safeher.patient_privacy_preferences`

Privacy preferences (per patient).

_Source: `database/04_future_products.sql` · 11 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `patient_id` | `UUID` | PK, FK→docslot.patients |
| 2 | `requires_female_provider` | `BOOLEAN` | NOT NULL, default |
| 3 | `requires_female_only_facility` | `BOOLEAN` | NOT NULL, default |
| 4 | `requires_chaperone` | `BOOLEAN` | NOT NULL, default |
| 5 | `hide_from_family_phone` | `BOOLEAN` | NOT NULL, default |
| 6 | `secondary_phone` | `VARCHAR(15)` | — |
| 7 | `secondary_phone_use` | `VARCHAR(50)` | — |
| 8 | `preferred_pickup_landmarks` | `TEXT` | — |
| 9 | `notes` | `TEXT` | — |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 11 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Schema: `genericfirst`

> GenericFirst (future): generic drug intelligence
> Source: database/04

### `genericfirst.bioequivalence_studies`

Bioequivalence studies (research references).

_Source: `database/04_future_products.sql` · 14 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `study_id` | `UUID` | PK, default |
| 2 | `branded_drug_id` | `UUID` | FK→genericfirst.drugs |
| 3 | `generic_drug_id` | `UUID` | FK→genericfirst.drugs |
| 4 | `study_title` | `TEXT` | NOT NULL |
| 5 | `journal_name` | `VARCHAR(200)` | — |
| 6 | `publication_date` | `DATE` | — |
| 7 | `doi` | `VARCHAR(100)` | — |
| 8 | `pdf_url` | `TEXT` | — |
| 9 | `sample_size` | `INT` | — |
| 10 | `auc_ratio` | `DECIMAL(5,3)` | — |
| 11 | `cmax_ratio` | `DECIMAL(5,3)` | — |
| 12 | `confidence_interval_90` | `VARCHAR(50)` | — |
| 13 | `conclusion` | `TEXT` | — |
| 14 | `metadata` | `JSONB` | default |

### `genericfirst.drug_equivalence`

Equivalence mappings (branded → generic alternatives).

_Source: `database/04_future_products.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `equivalence_id` | `UUID` | PK, default |
| 2 | `branded_drug_id` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 3 | `generic_drug_id` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 4 | `equivalence_rating` | `VARCHAR(10)` | NOT NULL |
| 5 | `bioequivalence_study_url` | `TEXT` | — |
| 6 | `notes` | `TEXT` | — |
| 7 | `verified_by_pharma_expert` | `BOOLEAN` | NOT NULL, default |
| 8 | `verified_at` | `TIMESTAMPTZ` | — |

### `genericfirst.drug_interactions`

Drug interactions database.

_Source: `database/04_future_products.sql` · 8 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `interaction_id` | `UUID` | PK, default |
| 2 | `drug_id_1` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 3 | `drug_id_2` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 4 | `severity` | `VARCHAR(20)` | NOT NULL |
| 5 | `description` | `TEXT` | NOT NULL |
| 6 | `clinical_effect` | `TEXT` | — |
| 7 | `management_advice` | `TEXT` | — |
| 8 | `references_doi` | `VARCHAR(100)` | — |

### `genericfirst.drugs`

Master drug database.

_Source: `database/04_future_products.sql` · 22 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `drug_id` | `UUID` | PK, default |
| 2 | `active_ingredient` | `VARCHAR(200)` | NOT NULL |
| 3 | `brand_name` | `VARCHAR(200)` | — |
| 4 | `is_generic` | `BOOLEAN` | NOT NULL, default |
| 5 | `manufacturer` | `VARCHAR(200)` | — |
| 6 | `dosage_form` | `VARCHAR(50)` | — |
| 7 | `strength` | `VARCHAR(50)` | — |
| 8 | `pack_size` | `VARCHAR(50)` | — |
| 9 | `mrp` | `DECIMAL(10,2)` | — |
| 10 | `average_market_price` | `DECIMAL(10,2)` | — |
| 11 | `jan_aushadhi_price` | `DECIMAL(10,2)` | — |
| 12 | `cdsco_approved` | `BOOLEAN` | NOT NULL, default |
| 13 | `cdsco_approval_number` | `VARCHAR(50)` | — |
| 14 | `therapeutic_class` | `VARCHAR(100)` | — |
| 15 | `requires_prescription` | `BOOLEAN` | NOT NULL, default |
| 16 | `is_scheduled_drug` | `BOOLEAN` | NOT NULL, default |
| 17 | `schedule_classification` | `VARCHAR(10)` | — |
| 18 | `contraindications` | `TEXT` | — |
| 19 | `side_effects` | `TEXT` | — |
| 20 | `metadata` | `JSONB` | default |
| 21 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |
| 22 | `updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `genericfirst.pharmacy_availability`

Pharmacy availability (which pharmacies stock which generics).

_Source: `database/04_future_products.sql` · 7 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `availability_id` | `UUID` | PK, default |
| 2 | `pharmacy_tenant_id` | `UUID` | FK→platform.tenants, NOT NULL |
| 3 | `drug_id` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 4 | `in_stock` | `BOOLEAN` | NOT NULL, default |
| 5 | `quantity_available` | `INT` | — |
| 6 | `selling_price` | `DECIMAL(10,2)` | — |
| 7 | `last_updated_at` | `TIMESTAMPTZ` | NOT NULL, default |

### `genericfirst.prescription_suggestions`

Prescription suggestions log (track what was suggested vs.

_Source: `database/04_future_products.sql` · 10 columns_

| # | Column | Type | Constraints |
|---|--------|------|-------------|
| 1 | `suggestion_id` | `UUID` | PK, default |
| 2 | `prescription_id` | `UUID` | — |
| 3 | `doctor_id` | `UUID` | — |
| 4 | `branded_drug_id` | `UUID` | FK→genericfirst.drugs, NOT NULL |
| 5 | `suggested_generic_drug_id` | `UUID` | FK→genericfirst.drugs |
| 6 | `suggested_jan_aushadhi` | `BOOLEAN` | NOT NULL, default |
| 7 | `estimated_savings_inr` | `DECIMAL(10,2)` | — |
| 8 | `doctor_accepted` | `BOOLEAN` | — |
| 9 | `doctor_decline_reason` | `VARCHAR(200)` | — |
| 10 | `created_at` | `TIMESTAMPTZ` | NOT NULL, default |

---

## Conventions

- UUID PKs via `gen_random_uuid()`; `tenant_id` on product tables (CASCADE); `set_updated_at()` trigger; soft-delete via `deleted_at`
- Encrypted columns registered in `platform.encrypted_fields_registry` (incl. `commission.brokers.pan_number`)
- RLS on 5 sensitive docslot tables via `platform.current_tenant_id()`
- PCPNDT hard CHECKs; discount↔attribution mutual exclusivity trigger (09)
- Permission resolution: `platform.resolve_user_permissions()` once per request; deny-override > grant-override > role grant

## Regeneration

Regenerate on any schema change. Do not hand-edit — fix source SQL comments and regenerate.