---
name: backend-phase4-ai-noshow-seam
description: Phase-4 .NET→Python AI no-show risk read seam (GET /bookings/{id}/no-show-risk); FINAL PASS — Finding 1 (AI HTTP call inside RLS read tx) CLOSED via ISelfManagedTransaction two-phase split
metadata:
  type: project
---

Phase-4 cross-service slice: .NET booking read surface calls Python AI sibling for advisory no-show risk. No schema/RBAC/PHI-storage/payout/encryption change.

**Verdict: FINAL PASS** (originally PASS-WITH-CONDITIONS; the one MEDIUM condition is now closed & re-verified, see below).

**Finding 1 CLOSED (re-verified in-PR, 2026-06-29):** the AI HTTP call no longer runs inside the RLS read transaction. Fix mirrors the command-side ISelfManagedTransaction pattern I already blessed for payout/ABDM:
- `TenantScopeQueryBehavior` (Behaviors.cs:57) now does `if (request is ISelfManagedTransaction) return await next();` — query-side opt-out, identical semantics to the command-side UnitOfWorkBehavior:206.
- `GetBookingNoShowRiskQuery` is `IQuery<NoShowRiskDto>, ISelfManagedTransaction` (DocslotQueries.cs:58). Handler injects IUnitOfWork, two phases: Phase 1 opens its OWN `BeginTenantScopeAsync(q.TenantId)` JUST for GetNoShowFeaturesAsync (RLS), disposes it read-only (no commit → tx rolls back, SET LOCAL cleared, pooled conn released); 404 thrown AFTER scope closes if features null. Phase 2 `ai.PredictAsync(...)` with NO DB tx open. So a slow/hanging AI service pins no DB connection. ISelfManagedTransaction is a real marker (Behaviors.cs:191), already used by ExecutePayoutCommand + LinkAbdmRecordCommand.
- Independently verified: clean `--no-incremental` build 0 errors; targeted no-show test passes; full integration suite 211/211 (the TenantScopeQueryBehavior change regressed no other query).
- LESSON (still general): network/external I/O must not sit inside the tenant-scope query tx. The query pipeline now has a sanctioned escape hatch (ISelfManagedTransaction) for handlers that do non-DB I/O — the correct pattern for any future read handler with an external call.

LOW (deferred advisory, accepted): no circuit breaker on the AI HttpClient, only a per-call timeout — add Polly if the endpoint gets hot.

What's sound (verified):
- **Egress is non-PHI**: HttpAiNoShowClient (mediq.Infrastructure/Ai/AiNoShowClients.cs:22-25) sends ONLY `{bookingId}` (a UUID). AI service derives tenant_id from the validated JWT principal (ai_service/app/routers/predictions.py:81 fetch_booking(principal.tenant_id, body.bookingId)) and 404s cross-tenant — body tenant is NOT trusted. `age` the AI uses stays server-side; .NET adapter reads back only probability/band/modelName/modelVersion.
- **JWT forwarding sound + not leaked**: forwards inbound Authorization header (AiNoShowClients.cs:26-28); caller already `docslot.booking.read`-authorized; AI validates the SAME DocSlot HS256 JWT (ai_service/app/auth.py). Token not logged — adapter logs only bookingId+status; Program.cs:86 UseSerilogRequestLogging logs no request headers by default.
- **SSRF-safe**: BaseUrl is operator config (AiService:BaseUrl), path is the hardcoded literal `/predictions/no-show`. No user-controlled URL.
- **Fail-safe / no fabrication**: any failure (unreachable/timeout/non-2xx/null) → adapter returns null → DTO Available=false (handler DocslotQueries.cs). Never a 500, never a fabricated score.
- **Feature read tenant-scoped + non-PHI**: GetNoShowFeaturesAsync (BookingReadService.cs:136) = bookings⋈time_slots WHERE tenant_id=@t AND booking_id=@b; returns only LeadTimeDays/SlotHour/IsBehalfBooking. Cross-tenant/unknown → null → 404.
- **Permission reuse correct**: docslot.booking.read (risk is part of reading the booking). No new permission needed; not under-privileged.
- **.NET never touches ai.\*** — persistence + prediction audit are the AI service's responsibility (its own conn). Confirmed no ai.* SQL in .NET.

**THE condition (MEDIUM, availability not confidentiality):** the AI HTTP call runs INSIDE the open RLS read transaction. TenantScopeQueryBehavior (Behaviors.cs:49-56) wraps the ENTIRE query handler (next()) in a DB tx with SET LOCAL app.tenant_id. The new handler does GetNoShowFeaturesAsync (DB) THEN ai.PredictAsync (external HTTP, up to 5s) all inside that one next() — so a slow/hanging AI service pins the pooled DB connection for the whole call. Under a flood of these GETs the pool can drain, degrading the whole booking surface — contradicts "advisory, never on the critical path." Fix: fetch features inside the tenant scope, then make the AI HTTP call AFTER the scope disposes (split the handler so the DB tx closes before the network call). This is the general lesson: **external/network calls must not sit inside the RLS read transaction.** Re-check every future query handler that does I/O beyond the DB.

LOW/INFO: HTTP client has no circuit breaker (only per-call timeout) — a persistently-down AI service still costs one 5s wait + one pinned conn per request until it recovers; consider a breaker if this endpoint gets hot.
