---
name: backend-dashboard-contracts
description: Wave 2 .NET dashboard DTO/enum contracts — the canonical PHI/enum/idempotency conventions the frontend must mirror
metadata:
  type: project
---

`backend/mediq/mediq.SharedDataModel/Docslot/` holds the Wave 2 contract DTOs (no handlers yet, contract-only).

These are the canonical, correct conventions — when frontend and backend disagree, these win (they cite SQL + DPDP + ADR-007 explicitly):

- `Dashboard/Dtos/BookingListItemDto.cs` exposes ONLY `MaskedPhone` (never raw) with DPDP rationale; revealing full phone is a separate purpose-of-use-gated read.
- `Dashboard/DashboardContract.cs` documents the cross-cutting rules: response envelope reuse from mediq.Utilities, Asia/Kolkata DateTimeOffset, PHI masking, idempotency replay (WasReplayed), enum wire-values = exact snake_case from SQL CHECK.
- Enums match SQL CHECK exactly: `BookingStatus` = 6 values incl. `rescheduled`; `BookingSource` (booked_via) = 5 values `whatsapp/dashboard/api/walk_in/phone_call`; `Gender` = `male/female/other/prefer_not_say` (nullable). `Language` = free VARCHAR (no CHECK), enum captures only en/hi, callers tolerate unknown strings.
- `IIdempotentRequest` + `ApprovalActionRequest` carry IdempotencyKey on the body; validation pipeline rejects null key (422) and requires Reason when Action=Cancel.
- `BookingActionResultDto.WasReplayed` distinguishes idempotent replay.

**Why:** the frontend mock contracts (`frontend/src/lib/mock/contracts.ts`) were built to mirror these but drifted (see [[drift-watchlist]]).
**How to apply:** treat these DTOs as the reference when judging frontend contract correctness; flag any frontend schema that exposes more PHI or fewer/different enum values than these.
