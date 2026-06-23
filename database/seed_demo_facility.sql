-- Apollo Care facility settings (idempotent, superuser). Backs the Settings screen
-- (business hours + appointment rules + WhatsApp/HFR connection). whatsapp_business_phone_id
-- matches the WhatsApp inbound tenant map ('PNID_APOLLO').
\set tenant_id '11111111-1111-1111-1111-111111111111'

INSERT INTO docslot.healthcare_facilities
  (facility_id, tenant_id, facility_type, specialty_focus,
   whatsapp_business_phone_id, whatsapp_verified_at, hfr_id, hfr_status,
   business_hours, appointment_settings, created_at, updated_at)
VALUES (
  md5('apollo-facility')::uuid, :'tenant_id', 'hospital', 'multi_specialty',
  'PNID_APOLLO', NOW() - interval '40 days', 'HFR-MH-APOLLO-0001', 'verified',
  '{"mon":{"open":"09:00","close":"18:00","closed":false},
    "tue":{"open":"09:00","close":"18:00","closed":false},
    "wed":{"open":"09:00","close":"18:00","closed":false},
    "thu":{"open":"09:00","close":"18:00","closed":false},
    "fri":{"open":"09:00","close":"18:00","closed":false},
    "sat":{"open":"09:00","close":"14:00","closed":false},
    "sun":{"open":null,"close":null,"closed":true}}'::jsonb,
  '{"slotDurationMinutes":15,"bookingCutoffHours":2,"autoConfirm":true,
    "maxAdvanceDays":30,"allowOverbooking":false,"reminderHoursBefore":24,
    "noShowGraceMinutes":15}'::jsonb,
  NOW(), NOW())
ON CONFLICT (facility_id) DO UPDATE
  SET business_hours = EXCLUDED.business_hours,
      appointment_settings = EXCLUDED.appointment_settings,
      updated_at = NOW();

SELECT facility_type, specialty_focus, hfr_status,
       business_hours->'sat'->>'close' AS sat_close,
       appointment_settings->>'slotDurationMinutes' AS slot_min
FROM docslot.healthcare_facilities WHERE tenant_id = :'tenant_id';
