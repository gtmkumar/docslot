---
name: brokers-global-identity
description: commission.brokers is a platform-global identity keyed by a UNIQUE phone; broker has no tenant_id, tenant linkage is via broker_tenant_links.
metadata:
  type: project
---

`commission.brokers` (`database/07_commission_broker.sql:47`) is a PLATFORM-GLOBAL identity:
- `phone VARCHAR(15) NOT NULL UNIQUE` — canonical Indian identity, unique across the WHOLE table (not per-tenant).
- The broker row itself has NO `tenant_id`. A broker's relationship to tenants lives in `commission.broker_tenant_links (broker_id, tenant_id)` (UNIQUE per pair).

Implication for the hidden-Care-Partner nudge (`run_partner_nudge_sweep`): the broker-exclusion match is digits-only and GLOBAL (`regexp_replace(br.phone,...) = regexp_replace(wcp.phone,...)` with `LIMIT 1`). This is CORRECT and intentionally safer than a tenant-scoped match: a number registered as a broker in ANY tenant is excluded from being nudged as a "hidden" partner everywhere. The `LIMIT 1` without ORDER BY is safe because `phone` is globally UNIQUE — the digits-normalized subquery returns at most one row, except the pathological case of two brokers whose stored phones differ only by punctuation (e.g. '+919...' vs '0919...'). Low risk; noted, not blocking.
