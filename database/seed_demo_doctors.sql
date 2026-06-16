-- Demo operational data for Apollo Care tenant (idempotent). Superuser (bypasses RLS).
-- Seeds departments + doctors so the live Doctors screen shows real cards.
\set tenant_id '11111111-1111-1111-1111-111111111111'

-- Departments (stable ids derived from names via md5→uuid).
INSERT INTO docslot.departments (department_id, tenant_id, name)
SELECT md5('apollo-dept-'||d)::uuid, :'tenant_id', d
FROM (VALUES ('Cardiology'),('Orthopedics'),('Gynaecology'),('Paediatrics'),
             ('Dermatology'),('ENT'),('General Medicine')) AS x(d)
ON CONFLICT (department_id) DO NOTHING;

-- Doctors (stable ids), each tied to a department.
INSERT INTO docslot.doctors
    (doctor_id, tenant_id, full_name, display_name, gender, department_id,
     specialization, experience_years, consultation_fee, is_active, is_accepting_new_patients)
SELECT md5('apollo-doc-'||fn)::uuid, :'tenant_id', fn, fn, g,
       md5('apollo-dept-'||dept)::uuid, spec, exp, fee, true, accepting
FROM (VALUES
  ('Dr. Anjali Sharma','female','Cardiology','Interventional Cardiology',14,900,true),
  ('Dr. Rohan Iyer','male','Cardiology','Cardiology',9,750,true),
  ('Dr. Priya Nair','female','Gynaecology','Obstetrics & Gynaecology',16,1100,true),
  ('Dr. Vikram Bose','male','Orthopedics','Joint Replacement',12,850,true),
  ('Dr. Meera Krishnan','female','Paediatrics','Neonatology',11,700,true),
  ('Dr. Saurabh Gupta','male','Dermatology','Cosmetic Dermatology',8,800,false),
  ('Dr. Faisal Khan','male','ENT','Otolaryngology',10,650,true),
  ('Dr. Lakshmi Rao','female','General Medicine','Internal Medicine',18,600,true)
) AS d(fn,g,dept,spec,exp,fee,accepting)
ON CONFLICT (doctor_id) DO NOTHING;

SELECT count(*) AS doctors_in_apollo FROM docslot.doctors WHERE tenant_id = :'tenant_id';
