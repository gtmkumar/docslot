-- Reset Apollo Care demo bookings/slots/patients to the canonical seeded baseline
-- after live write-path verification drifted them. Idempotent, superuser (bypasses RLS).
\set tenant_id '11111111-1111-1111-1111-111111111111'

-- Restore each booking's canonical status + the matching timestamps.
UPDATE docslot.bookings b SET
    status = v.status,
    confirmed_at = CASE WHEN v.status='confirmed' THEN COALESCE(b.confirmed_at, NOW()) ELSE NULL END,
    no_show_at  = CASE WHEN v.status='no_show'   THEN COALESCE(b.no_show_at, NOW())  ELSE NULL END,
    cancelled_at = NULL, cancellation_reason = NULL
FROM (VALUES
  (md5('apollo-bk-+919820000072Dr. Anjali Sharma')::uuid,'pending'),
  (md5('apollo-bk-+919820000012Dr. Rohan Iyer')::uuid,'pending'),
  (md5('apollo-bk-+919820000034Dr. Priya Nair')::uuid,'pending'),
  (md5('apollo-bk-+919820000081Dr. Priya Nair')::uuid,'pending'),
  (md5('apollo-bk-+919820000032Dr. Meera Krishnan')::uuid,'confirmed'),
  (md5('apollo-bk-+919820000020Dr. Vikram Bose')::uuid,'confirmed'),
  (md5('apollo-bk-+919820000089Dr. Saurabh Gupta')::uuid,'confirmed'),
  (md5('apollo-bk-+919820000011Dr. Lakshmi Rao')::uuid,'completed'),
  (md5('apollo-bk-+919820000023Dr. Lakshmi Rao')::uuid,'completed'),
  (md5('apollo-bk-+919820000014Dr. Faisal Khan')::uuid,'no_show')
) AS v(booking_id, status)
WHERE b.booking_id = v.booking_id;

-- Restore slot occupancy: only slots that actually back a booking are 'booked';
-- every other today slot (the bookable afternoon inventory) returns to 'available'.
UPDATE docslot.time_slots ts SET current_count = 1, status = 'booked'
WHERE ts.tenant_id = :'tenant_id'
  AND ts.slot_id IN (SELECT slot_id FROM docslot.bookings WHERE tenant_id = :'tenant_id');
UPDATE docslot.time_slots ts SET current_count = 0, status = 'available'
WHERE ts.tenant_id = :'tenant_id' AND ts.slot_date = CURRENT_DATE
  AND ts.slot_id NOT IN (SELECT slot_id FROM docslot.bookings WHERE tenant_id = :'tenant_id');

-- Remove any patient added during live verification (anything linked to Apollo that
-- isn't one of the 10 canonical seeded patients), and its now-orphan link.
DELETE FROM docslot.patient_tenant_links l
WHERE l.tenant_id = :'tenant_id'
  AND l.patient_id NOT IN (
    SELECT md5('apollo-pat-'||ph)::uuid FROM (VALUES
      ('+919820000072'),('+919820000012'),('+919820000034'),('+919820000032'),('+919820000020'),
      ('+919820000089'),('+919820000011'),('+919820000081'),('+919820000014'),('+919820000023')
    ) AS p(ph));

SELECT status, count(*) FROM docslot.bookings WHERE tenant_id = :'tenant_id' GROUP BY status ORDER BY status;
