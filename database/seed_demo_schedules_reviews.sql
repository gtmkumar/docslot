-- Enrichment seed for doctor cards (idempotent, superuser). Schedules (today's hours)
-- + published reviews (ratings). Run after seed_demo_doctors/_patients/_bookings.
\set tenant_id '11111111-1111-1111-1111-111111111111'

-- Schedules: every weekday (DOW 0..6) 09:00–17:00 for each seeded doctor, so "today"
-- resolves to hours regardless of the DOW convention the read query uses.
INSERT INTO docslot.doctor_schedules (doctor_id, day_of_week, start_time, end_time, is_active)
SELECT md5('apollo-doc-'||fn)::uuid, dow, '09:00'::time, '17:00'::time, true
FROM (VALUES ('Dr. Anjali Sharma'),('Dr. Rohan Iyer'),('Dr. Priya Nair'),('Dr. Vikram Bose'),
             ('Dr. Meera Krishnan'),('Dr. Saurabh Gupta'),('Dr. Faisal Khan'),('Dr. Lakshmi Rao')) AS d(fn)
CROSS JOIN generate_series(0,6) AS dow
ON CONFLICT DO NOTHING;

-- One published review per seeded booking → every doctor gets a rating (4–5).
INSERT INTO docslot.reviews (booking_id, patient_id, doctor_id, tenant_id, rating, is_published, is_verified)
SELECT md5('apollo-bk-'||ph||doc)::uuid, md5('apollo-pat-'||ph)::uuid, md5('apollo-doc-'||doc)::uuid,
       :'tenant_id', rating, true, true
FROM (VALUES
  ('+919820000072','Dr. Anjali Sharma',5),
  ('+919820000012','Dr. Rohan Iyer',5),
  ('+919820000034','Dr. Priya Nair',5),
  ('+919820000081','Dr. Priya Nair',4),
  ('+919820000032','Dr. Meera Krishnan',5),
  ('+919820000020','Dr. Vikram Bose',5),
  ('+919820000089','Dr. Saurabh Gupta',4),
  ('+919820000011','Dr. Lakshmi Rao',5),
  ('+919820000023','Dr. Lakshmi Rao',4),
  ('+919820000014','Dr. Faisal Khan',5)
) AS r(ph,doc,rating)
ON CONFLICT DO NOTHING;

SELECT (SELECT count(*) FROM docslot.doctor_schedules) schedules,
       (SELECT count(*) FROM docslot.reviews WHERE tenant_id = :'tenant_id') reviews;
