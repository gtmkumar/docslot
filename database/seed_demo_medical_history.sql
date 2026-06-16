-- Patient medical history for Apollo Care demo (idempotent, superuser bypasses RLS).
-- The PHI corpus the AI service's RAG indexes + answers over. record_type ∈
-- {allergy,chronic_condition,surgery,medication,vaccination,family_history,lifestyle}.
\set tenant_id '11111111-1111-1111-1111-111111111111'

INSERT INTO docslot.patient_medical_history
  (history_id, patient_id, tenant_id, record_type, title, description, severity, icd10_code, is_active, is_critical, started_date, added_at, metadata)
SELECT md5('apollo-mh-'||ph||rtype||title)::uuid, md5('apollo-pat-'||ph)::uuid, :'tenant_id',
       rtype, title, descr, sev, icd, true, crit, sdate::date, NOW(), '{}'::jsonb
FROM (VALUES
  -- Riya Kapoor (31F)
  ('+919820000072','chronic_condition','Essential hypertension','Diagnosed 2022; BP controlled on medication, target <130/80.','moderate','I10',false,'2022-03-01'),
  ('+919820000072','allergy','Penicillin allergy','Anaphylaxis to amoxicillin in 2019. Avoid all beta-lactams.','severe',NULL,true,'2019-06-01'),
  ('+919820000072','medication','Amlodipine 5mg','Once daily for hypertension. Good tolerance.',NULL,NULL,false,'2022-03-15'),
  -- Aman Shah (42M)
  ('+919820000012','chronic_condition','Type 2 diabetes mellitus','HbA1c 7.8%. On oral hypoglycemics, diet-controlled.','moderate','E11',false,'2020-11-01'),
  ('+919820000012','medication','Metformin 1000mg','Twice daily with meals for glycemic control.',NULL,NULL,false,'2020-11-10'),
  ('+919820000012','lifestyle','Tobacco use','Smokes ~10 cigarettes/day for 15 years. Counseled on cessation.','moderate',NULL,false,'2010-01-01'),
  -- Pooja Singh (56F)
  ('+919820000020','chronic_condition','Bilateral knee osteoarthritis','Grade 3 OA, chronic pain managed with physiotherapy.','moderate','M17',false,'2021-01-01'),
  ('+919820000020','surgery','Right total knee replacement','Performed Jan 2023, uneventful recovery.',NULL,NULL,false,'2023-01-20'),
  -- Nikhil Bhatt (50M)
  ('+919820000011','chronic_condition','Coronary artery disease','Single-vessel disease, stable angina.','severe','I25',true,'2022-05-01'),
  ('+919820000011','surgery','Coronary angioplasty with stent','LAD stent placed May 2022. On dual antiplatelet therapy.',NULL,NULL,false,'2022-05-15'),
  ('+919820000011','family_history','Father — myocardial infarction','Father had a heart attack at age 58.',NULL,NULL,false,NULL),
  -- Meera Joshi (64F)
  ('+919820000023','chronic_condition','Hypothyroidism','On replacement therapy, TSH normalized.','mild','E03',false,'2018-09-01'),
  ('+919820000023','medication','Levothyroxine 75mcg','Once daily on empty stomach.',NULL,NULL,false,'2018-09-10'),
  ('+919820000023','allergy','Sulfa drug allergy','Rash with sulfonamides. Avoid co-trimoxazole.','moderate',NULL,false,'2015-01-01'),
  -- Karan Mehta (8M, paediatric)
  ('+919820000032','allergy','Peanut allergy','Severe anaphylaxis risk. Carries epinephrine auto-injector.','critical',NULL,true,'2020-01-01'),
  ('+919820000032','chronic_condition','Childhood asthma','Mild intermittent, salbutamol PRN.','mild','J45',false,'2021-06-01'),
  ('+919820000032','vaccination','MMR vaccination complete','Both doses administered per schedule.',NULL,NULL,false,'2019-01-01'),
  -- Harsh Patel (19M)
  ('+919820000014','surgery','Appendectomy','Laparoscopic appendectomy 2021, no complications.',NULL,NULL,false,'2021-08-01'),
  ('+919820000014','allergy','Dust mite allergy','Seasonal allergic rhinitis.','mild',NULL,false,'2018-01-01')
) AS m(ph,rtype,title,descr,sev,icd,crit,sdate)
ON CONFLICT (history_id) DO NOTHING;

SELECT record_type, count(*) FROM docslot.patient_medical_history WHERE tenant_id = :'tenant_id' GROUP BY record_type ORDER BY record_type;
