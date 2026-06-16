-- Demo patients for Apollo Care (idempotent, superuser bypasses RLS).
-- patients are cross-tenant (phone = global id); access is link-mediated via
-- patient_tenant_links, which is how the API list query scopes them.
\set tenant_id '11111111-1111-1111-1111-111111111111'

INSERT INTO docslot.patients (patient_id, phone_number, full_name, age, gender, preferred_language)
SELECT md5('apollo-pat-'||ph)::uuid, ph, fn, ag, g, lang
FROM (VALUES
  ('+919820000072','Riya Kapoor',31,'female','en'),
  ('+919820000012','Aman Shah',42,'male','en'),
  ('+919820000034','Sneha Reddy',28,'female','hi'),
  ('+919820000032','Karan Mehta',8,'male','hi'),
  ('+919820000020','Pooja Singh',56,'female','hi'),
  ('+919820000089','Aditya Pillai',34,'male','en'),
  ('+919820000011','Nikhil Bhatt',50,'male','en'),
  ('+919820000081','Tanvi Iyer',26,'female','en'),
  ('+919820000014','Harsh Patel',19,'male','hi'),
  ('+919820000023','Meera Joshi',64,'female','hi')
) AS p(ph,fn,ag,g,lang)
ON CONFLICT (patient_id) DO NOTHING;

INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits)
SELECT gen_random_uuid(), md5('apollo-pat-'||ph)::uuid, :'tenant_id', NOW() - (rnd||' days')::interval, NOW(), vis
FROM (VALUES
  ('+919820000072',3,'120'),('+919820000012',5,'90'),('+919820000034',2,'200'),
  ('+919820000032',1,'45'),('+919820000020',7,'30'),('+919820000089',4,'60'),
  ('+919820000011',6,'12'),('+919820000081',2,'8'),('+919820000014',1,'5'),
  ('+919820000023',8,'15')
) AS l(ph,vis,rnd)
ON CONFLICT DO NOTHING;

SELECT count(*) AS patients_linked_to_apollo
FROM docslot.patient_tenant_links WHERE tenant_id = :'tenant_id';
