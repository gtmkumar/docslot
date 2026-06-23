---
name: platform-admin-vs-tenant-login
description: Use admin@docslot.io/admin (platform-admin) to verify platform-admin screens (Developers, Security) in LIVE mode — priyanka (tenant_owner) gets 403 + no nav for them
metadata:
  type: feedback
---

When verifying PLATFORM-ADMIN screens (Developers `/developers`, Security `/security`) against the LIVE API, log in as **admin@docslot.io / admin**, NOT priyanka@apollocare.in/reception.

**Why:** these screens + their `developers`/`security` menu nodes are platform-admin-only. The live backend-driven nav (`/me/menus`) only returns those nodes for the platform-admin (12 top nodes); a tenant_owner like priyanka gets neither the menu entries NOR access (403 on the endpoints). Verifying with priyanka in live mode would wrongly look like the screens "don't work". The MOCK seam is a SEPARATE fixture that DOES grant priyanka these menus — so in mock mode priyanka sees them, which masks the live difference. The two persona/RBAC sets are independent.

**How to apply:** For any screen gated to platform-admin (or whenever a live `/me/menus` check is part of QA), pick the login by who the backend grants the relevant menu/permission to — admin@docslot.io for platform surfaces, priyanka@apollocare.in for tenant-scoped surfaces (bookings/patients/doctors/calendar/analytics/care-partners). When proving backend-driven nav HIDES a node, log in as the persona who SHOULDN'T have it and assert its absence. See [[live-api-seam]].
