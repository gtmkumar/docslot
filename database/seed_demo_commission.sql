-- Commission/broker demo data for Apollo Care (idempotent, superuser bypasses RLS).
-- PAN is encrypted at the app layer, so seed pan_number NULL and set the *_verified
-- flags directly. Run after seed_demo_bookings (attributions FK docslot.bookings).
\set tenant_id '11111111-1111-1111-1111-111111111111'

-- Brokers (Care Partners) — names mirror the prototype screen.
INSERT INTO commission.brokers
  (broker_id, phone, full_name, broker_type, tier_level, pan_verified, gst_verified,
   is_active, can_refer_pndt, requires_consent_for_phi, payout_method,
   blacklisted_at, blacklist_reason, created_at, updated_at)
SELECT md5('apollo-brk-'||fn)::uuid, ph, fn, btype, tier, panv, gstv, active, false, true, 'upi',
       CASE WHEN blk THEN NOW() ELSE NULL END,
       CASE WHEN blk THEN 'Repeated self-referral fraud signals' ELSE NULL END,
       NOW(), NOW()
FROM (VALUES
  ('Ravi Deshmukh','+919820550023','medical_rep','silver',true,false,true,false),
  ('Sunita Corporate Wellness','+919820550056','corporate_hr','gold',true,true,true,false),
  ('Imran Panel Coordinator','+919820550089','insurance_panel','platinum',true,true,true,false),
  ('Local Navigator','+919820550014','community_worker','basic',false,false,false,true)
) AS b(fn,ph,btype,tier,panv,gstv,active,blk)
ON CONFLICT (phone) DO NOTHING;

-- Link every broker to Apollo Care.
INSERT INTO commission.broker_tenant_links (link_id, broker_id, tenant_id, is_active, activated_at, created_at, updated_at)
SELECT gen_random_uuid(), md5('apollo-brk-'||fn)::uuid, :'tenant_id', true, NOW(), NOW(), NOW()
FROM (VALUES ('Ravi Deshmukh'),('Sunita Corporate Wellness'),('Imran Panel Coordinator'),('Local Navigator')) AS b(fn)
ON CONFLICT (broker_id, tenant_id) DO NOTHING;

-- Commission rules (active).
INSERT INTO commission.commission_rules
  (rule_id, tenant_id, rule_name, rule_key, calc_type, flat_amount_inr, percentage,
   min_commission_inr, max_commission_inr, max_monthly_per_broker_inr, priority, excludes_pndt, is_active, created_at, updated_at)
SELECT md5('apollo-rule-'||rkey)::uuid, :'tenant_id', rname, rkey, ctype, flat, pct, mn, mx, cap, prio, true, true, NOW(), NOW()
FROM (VALUES
  ('Standard consult flat','std_consult_flat','flat',150,NULL,NULL,NULL,15000,10),
  ('Specialist percentage','specialist_pct','percentage',NULL,8.0,100,500,25000,20),
  ('Corporate tier bonus','corporate_tier','flat',250,NULL,NULL,NULL,40000,5)
) AS r(rname,rkey,ctype,flat,pct,mn,mx,cap,prio)
ON CONFLICT (tenant_id, rule_key) DO NOTHING;

-- Attributions tying bookings → brokers (varied verification + commission status).
INSERT INTO commission.attributions
  (attribution_id, tenant_id, booking_id, broker_id, rule_id, attribution_source,
   verification_status, commission_status, commission_amount_inr, fraud_score, fraud_flags,
   attributed_at, created_at, updated_at)
SELECT md5('apollo-attr-'||bkkey)::uuid, :'tenant_id', md5('apollo-bk-'||bkkey)::uuid,
       md5('apollo-brk-'||brk)::uuid, md5('apollo-rule-'||rkey)::uuid, src, vstat, cstat, amt, fraud, flags,
       NOW(), NOW(), NOW()
FROM (VALUES
  ('+919820000072Dr. Anjali Sharma','Ravi Deshmukh','specialist_pct','referral_link','auto_verified','earned',72.00,0.05,'{}'::varchar[]),
  ('+919820000032Dr. Meera Krishnan','Sunita Corporate Wellness','std_consult_flat','broker_portal_booking','patient_confirmed','ready_to_pay',150.00,0.10,'{}'::varchar[]),
  ('+919820000011Dr. Lakshmi Rao','Imran Panel Coordinator','corporate_tier','post_hoc_claim','patient_confirmed','paid',250.00,0.02,'{}'::varchar[]),
  ('+919820000089Dr. Saurabh Gupta','Ravi Deshmukh','specialist_pct','referral_link','pending','pending',64.00,0.78,'{repeat_phone}'::varchar[])
) AS a(bkkey,brk,rkey,src,vstat,cstat,amt,fraud,flags)
ON CONFLICT (booking_id, broker_id) DO NOTHING;

-- Payouts (pending + approved; TDS 5% u/s 194H).
INSERT INTO commission.payouts
  (payout_id, tenant_id, broker_id, period_start, period_end, attribution_count,
   gross_amount_inr, tds_rate, tds_amount_inr, gst_rate, gst_amount_inr, net_amount_inr,
   status, payment_method, initiated_at, created_at, updated_at)
SELECT md5('apollo-payout-'||brk)::uuid, :'tenant_id', md5('apollo-brk-'||brk)::uuid,
       date_trunc('month', CURRENT_DATE)::date, (date_trunc('month', CURRENT_DATE) + interval '1 month - 1 day')::date,
       cnt, gross, 5.00, round(gross*0.05,2), gst_rate, gst_amt, net, st, 'upi', NOW(), NOW(), NOW()
FROM (VALUES
  ('Sunita Corporate Wellness',1,150.00,18.00,27.00,169.50,'pending'),
  ('Imran Panel Coordinator',1,250.00,18.00,45.00,282.50,'approved')
) AS p(brk,cnt,gross,gst_rate,gst_amt,net,st)
ON CONFLICT (payout_id) DO NOTHING;

SELECT (SELECT count(*) FROM commission.brokers b JOIN commission.broker_tenant_links l ON l.broker_id=b.broker_id WHERE l.tenant_id=:'tenant_id') brokers,
       (SELECT count(*) FROM commission.commission_rules WHERE tenant_id=:'tenant_id') rules,
       (SELECT count(*) FROM commission.attributions WHERE tenant_id=:'tenant_id') attributions,
       (SELECT count(*) FROM commission.payouts WHERE tenant_id=:'tenant_id') payouts;
