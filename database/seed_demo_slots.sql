-- Available bookable slots for Apollo Care doctors (idempotent, superuser).
-- The booking wizard needs AVAILABLE slots to hold+convert; also populates each
-- doctor's nextAvailableSlot card field. Afternoon times avoid the booked morning slots.
\set tenant_id '11111111-1111-1111-1111-111111111111'

INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, max_count, current_count)
SELECT md5('apollo-free-'||fn||d::text||st)::uuid, :'tenant_id', md5('apollo-doc-'||fn)::uuid,
       CURRENT_DATE + d, st::time, et::time, 'available', 1, 0
FROM (VALUES ('Dr. Anjali Sharma'),('Dr. Rohan Iyer'),('Dr. Priya Nair'),('Dr. Vikram Bose'),
             ('Dr. Meera Krishnan'),('Dr. Saurabh Gupta'),('Dr. Faisal Khan'),('Dr. Lakshmi Rao')) AS doc(fn)
CROSS JOIN generate_series(0,2) AS d            -- today, tomorrow, day-after
CROSS JOIN (VALUES ('15:00','15:15'),('15:30','15:45'),('16:00','16:15'),
                   ('16:30','16:45'),('17:00','17:15')) AS t(st,et)
ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING;

SELECT count(*) AS available_slots
FROM docslot.time_slots WHERE tenant_id = :'tenant_id' AND status = 'available';
