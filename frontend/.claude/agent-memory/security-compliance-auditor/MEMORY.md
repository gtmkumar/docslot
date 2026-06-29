# Security & Compliance Auditor — Frontend Memory Index

- [Frontend Form16A full-PAN handling](frontend-form16a-pan.md) — the transient-blob pattern for the full-PAN TDS document, and the leak vectors verified absent. Audited PASS on feat/phase2-broker-portal-frontend.
- [Frontend RBAC gating pattern](frontend-rbac-gating.md) — usePermissions().can() backed by /me/permissions; mock seed grants are mock-only; no role-in-JSX. IDOR-safe /commission/me paths.
