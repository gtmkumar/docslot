# DocSlot — Security & Compliance Overview

> Summary view. Canonical implementations: `database/05_security_hardening.sql` (13 tables), PRODUCTION_SPEC.md security appendices, RBAC_NAVIGATION.md, COMMISSION_SYSTEM.md legal section.

## Threat model (top risks → controls)

| Threat | Control |
|---|---|
| Cross-tenant data leak | RLS on sensitive tables via `platform.current_tenant_id()`; tenant_id scoping on every product table; deny-wins RBAC |
| PHI exposure (staff snooping) | Purpose-of-use declaration on medical access; break-glass with mandatory justification + alert; column-level encryption registry |
| Audit tampering | Hash-chained append-only audit log (each row hashes the previous) |
| Token/key compromise | Scoped short-lived JWTs; key registry with rotation fields; secrets only in GHA secrets / VPS env |
| Referral-fraud (commission) | Attribution verification states, fraud_score + flags, discount↔attribution exclusivity trigger, payout approval gate |
| Regulatory (PCPNDT/MCI/DPDP) | DB CHECK constraints (cannot be code-bypassed); marketing-fee positioning; consent records; cryptographic erasure (ADR-005) |
| WhatsApp webhook spoofing | Meta signature verification; outbox-only sends; template approval |

## DPDP Act 2023 mapping

Consent records per data class · `encrypted_fields_registry` declares every encrypted column with legal basis · data-principal rights tables (access/correction/erasure requests) · erasure via key destruction (Section 12) · breach reporting tables with CERT-In timeline fields.

## Secrets & keys

App-layer field encryption (AES-GCM) with keys referenced in `platform.encryption_keys` (KMS provider pluggable). Never store: full Aadhaar, raw card data, plaintext PAN. PAN/bank fields registered + encrypted (legal_obligation).

## Security gates in delivery

`security-compliance-auditor` agent holds veto on every wave (AGENT_TEAM.md). Mandatory checks: new tables carry tenant_id + RLS evaluation; new permissions reviewed for scope/danger flags; any patient-data read path declares purpose; payouts require approval permission distinct from execution.

## Reporting

Single-maintainer project: report issues directly to the owner. No public bug bounty.
