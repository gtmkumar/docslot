---
name: dashboard-api-contracts
description: Wave 2 DocSlot dashboard read-model DTO/enum contracts in mediq.SharedDataModel — what they mirror in canonical SQL and the cross-cutting conventions the frontend must mirror.
metadata:
  type: project
---

Wave 2 (contract-first, no endpoints) locked the reception-desk Dashboard read-model contracts in `backend/mediq/mediq.SharedDataModel/Docslot/`.

**Why:** A React dashboard is built in parallel against a mock seam; these DTOs are the `api-contracts` the frontend mirrors and the eventual API must match. Canonical source of truth is the PostgreSQL schema in `database/` (ADR-007: SQL wins over markdown/briefs).

**How to apply:** When building dashboard endpoints/handlers/EF later, project onto these existing DTOs — do not redefine. Verify the files still exist (`Docslot/Dashboard/`, `Docslot/Navigation/`) before relying on them.

Key facts to remember:
- **Enum values came from SQL CHECK constraints, NOT the orchestrator brief** (brief was wrong/abbreviated). `BookingStatus` has 6 values incl. `rescheduled` (brief said 5). `BookingSource` mirrors `bookings.booked_via` = whatsapp/dashboard/api/walk_in/phone_call (brief said whatsapp/walk-in/phone). `Gender` = male/female/other/prefer_not_say. `Language` (en/hi) is NOT CHECK-constrained in SQL (free VARCHAR(10)) — tolerate unknown values defensively.
- Enums use `[EnumMember(Value="snake_case")]` carrying the exact DB token; serializer must round-trip the string, not member name/number.
- **DRY:** SharedDataModel had ZERO files before this wave. DTOs are payloads only; the API wraps them in `mediq.Utilities`' existing `SingleResponse<T>` / `PaginatedListResponse<T>` (build via `mediq.Utilities.Common.PaginatedList<T>`), errors via `Message`+`ErrorMessageEnum`, handlers return `mediq.Utilities.Results.Result<T>`. SharedDataModel has no package refs and must stay dependency-free (pure records).
- **Timezone:** all user-facing instants are `DateTimeOffset`; slot times emitted at Asia/Kolkata +05:30. `DashboardContract` static class documents this + holds `TimeZoneId`/`TimeZoneOffset`/`CurrencyCode="INR"`.
- **PHI:** `BookingListItemDto` exposes only `MaskedPhone`; full phone/patient reads are a separate purpose-of-use-gated endpoint (deferred).
- **Idempotency:** `ApprovalActionRequest`/booking mutations implement `IIdempotentRequest` (mirrors `Idempotency-Key` header).
- Nav: `MenuNodeDto` mirrors `platform.get_user_menus()` flat rows assembled into a tree (bilingual Label/LabelHi, BadgeSource). `PermissionSetDto` mirrors `platform.resolve_user_permissions()` flat key set — resolve once per request, check in memory, no hardcoded roles.
