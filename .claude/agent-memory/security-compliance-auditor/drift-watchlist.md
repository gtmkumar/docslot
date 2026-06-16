---
name: drift-watchlist
description: Recurring frontend/contract anti-patterns to re-check every DocSlot wave (Wave 3 items remediated — kept as regression watchlist)
metadata:
  type: feedback
---

These four were live findings in the Wave 3 dashboard audit. ALL were remediated and re-verified PASS in the Wave 3 re-audit. Keep them as a REGRESSION watchlist — re-grep each wave to ensure they don't creep back.

1. **Raw phone in a list DTO.** FIXED: `frontend/src/lib/mock/contracts.ts BookingRowSchema` now carries `maskedPhone` (no raw phone); `listBookings()` emits masked only. Mirrors backend `BookingListItemDto.MaskedPhone`. Re-check: no raw `phone:` field reappears in any list/aggregate schema.
2. **cmdk `value` leaks PHI.** FIXED: `components/ui/CommandPalette.tsx` item `value` is now `${p.name} ${p.id}` (non-PHI); raw phone never enters the DOM/search index, only `maskPhone(p.phone)` renders. Re-check: no `p.phone` in any `value=`/searchable attr.
3. **Partial idempotency.** FIXED: all four hooks (`useApproveBooking/useCancelBooking/useSendPaymentLink/useCreateBooking` in `features/bookings/api.ts`) take `idempotencyKey` as a typed input field. Callers generate it OUTSIDE the mutationFn (in the event handler) so retries reuse the same key. Mock adapter (`lib/mock/index.ts withIdem()`) de-dupes per key. Re-check: any NEW money/booking hook must thread a caller-generated stable key, never generate inside mutationFn.
4. **Enum drift.** FIXED: `lib/types.ts` + `contracts.ts` now use canonical snake_case — status = 6 incl. `rescheduled`, source = `whatsapp/dashboard/api/walk_in/phone_call`. Defined once as `BookingStatusSchema`/`BookingSourceSchema` and reused. Re-check: enums stay snake_case and match SQL CHECK.

**Why:** in the first audit these passed casual review because comments asserted the control while code diverged. The fixes are real this time (verified line-by-line), but documentation-vs-code drift is the recurring failure mode here.
**How to apply:** never trust a "we mask/idempotent everything" comment — grep the actual mutation hooks, DTO schemas, and DOM attributes each wave.

**Permission-key namespace (verified Wave 3 re-audit):** frontend uses `docslot.`-prefixed keys that all exist in `database/03_docslot.sql:747-790`. NOTE: there is NO `docslot.patient.create` in the schema — patient registration is gated on `docslot.patient.update` (is_dangerous=true). "Add patient" in CommandPalette correctly uses `docslot.patient.update`. If a dedicated registration permission is wanted later, it must be SEEDED in the schema first (schema is source of truth), then referenced.
