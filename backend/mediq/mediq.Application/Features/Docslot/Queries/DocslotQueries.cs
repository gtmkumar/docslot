using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Queries;

/// <summary>
/// Resolves the booking READ scope from the caller's resolved permission set (Phase D §6). A tenant-wide reader
/// (<c>docslot.booking.read</c>, reception) sees everything; a self-scoped caller (<c>docslot.booking.read_self</c>,
/// a doctor) is confined to their own <c>docslot.doctors</c> row — the SAME identity derivation as consultation
/// finalize. Scope derives ONLY from the token/permissions, never a client-supplied doctorId (no widening).
/// </summary>
internal static class BookingReadScope
{
    public const string TenantWide = "docslot.booking.read";
    public const string SelfOnly = "docslot.booking.read_self";

    /// <summary>Returns the doctor_id to confine the read to, or null for a tenant-wide reader. Throws
    /// ForbiddenException if the caller holds neither key, or holds only read_self with no doctor profile.</summary>
    public static async Task<Guid?> ResolveDoctorFilterAsync(
        IPermissionContext perms, ICurrentUserContext ctx, IClinicalRepository clinical, Guid tenantId, CancellationToken ct)
    {
        if (perms.Has(TenantWide)) return null;   // reception — unchanged, full tenant view
        if (perms.Has(SelfOnly))
        {
            var userId = ctx.UserId ?? throw new ForbiddenException("No authenticated user.");
            return await clinical.GetDoctorByUserIdAsync(userId, tenantId, ct)
                ?? throw new ForbiddenException("No active doctor profile for this user; cannot list self-scoped bookings.");
        }
        throw new ForbiddenException("Missing booking read permission.");
    }
}

// ---- Dashboard summary ---------------------------------------------------------------------------

public sealed record GetDashboardSummaryQuery(Guid TenantId) : IQuery<DashboardSummaryDto>;

public sealed class GetDashboardSummaryQueryHandler(IBookingReadService reads)
    : IQueryHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    public Task<DashboardSummaryDto> Handle(GetDashboardSummaryQuery q, CancellationToken ct)
        => reads.GetSummaryAsync(q.TenantId, ct);
}

// ---- Dashboard side panels (agent / department load / floor) --------------------------------------

public sealed record GetAgentPanelQuery(Guid TenantId) : IQuery<AgentPanelDto>;

public sealed class GetAgentPanelQueryHandler(IDashboardPanelsReadService reads)
    : IQueryHandler<GetAgentPanelQuery, AgentPanelDto>
{
    public Task<AgentPanelDto> Handle(GetAgentPanelQuery q, CancellationToken ct)
        => reads.GetAgentPanelAsync(q.TenantId, ct);
}

public sealed record GetDepartmentLoadQuery(Guid TenantId) : IQuery<IReadOnlyList<DepartmentLoadDto>>;

public sealed class GetDepartmentLoadQueryHandler(IDashboardPanelsReadService reads)
    : IQueryHandler<GetDepartmentLoadQuery, IReadOnlyList<DepartmentLoadDto>>
{
    public Task<IReadOnlyList<DepartmentLoadDto>> Handle(GetDepartmentLoadQuery q, CancellationToken ct)
        => reads.GetDepartmentLoadAsync(q.TenantId, ct);
}

public sealed record GetFloorDoctorsQuery(Guid TenantId) : IQuery<IReadOnlyList<FloorDoctorDto>>;

public sealed class GetFloorDoctorsQueryHandler(IDashboardPanelsReadService reads)
    : IQueryHandler<GetFloorDoctorsQuery, IReadOnlyList<FloorDoctorDto>>
{
    public Task<IReadOnlyList<FloorDoctorDto>> Handle(GetFloorDoctorsQuery q, CancellationToken ct)
        => reads.GetFloorDoctorsAsync(q.TenantId, ct);
}

// ---- Analytics -----------------------------------------------------------------------------------

public sealed record GetAnalyticsQuery(Guid TenantId, AnalyticsPeriod Period) : IQuery<AnalyticsDto>;

public sealed class GetAnalyticsQueryHandler(IAnalyticsReadService reads)
    : IQueryHandler<GetAnalyticsQuery, AnalyticsDto>
{
    public Task<AnalyticsDto> Handle(GetAnalyticsQuery q, CancellationToken ct)
        => reads.GetAnalyticsAsync(q.TenantId, q.Period, ct);
}

// ---- Bookings list / detail / conversation -------------------------------------------------------

public sealed record ListBookingsQuery(BookingListFilter Filter)
    : IQuery<(IReadOnlyList<BookingListItemDto> Items, int Total)>;

public sealed class ListBookingsQueryHandler(
    IBookingReadService reads, IPermissionContext perms, ICurrentUserContext ctx, IClinicalRepository clinical)
    : IQueryHandler<ListBookingsQuery, (IReadOnlyList<BookingListItemDto> Items, int Total)>
{
    public async Task<(IReadOnlyList<BookingListItemDto> Items, int Total)> Handle(ListBookingsQuery q, CancellationToken ct)
    {
        var doctorFilter = await BookingReadScope.ResolveDoctorFilterAsync(perms, ctx, clinical, q.Filter.TenantId, ct);
        // Self-scoped caller: FORCE the doctor filter (overriding any client-supplied doctorId — no widening).
        var filter = doctorFilter is { } docId ? q.Filter with { DoctorId = docId } : q.Filter;
        return await reads.ListAsync(filter, ct);
    }
}

public sealed record GetBookingQuery(Guid TenantId, Guid BookingId) : IQuery<BookingListItemDto>;

public sealed class GetBookingQueryHandler(
    IBookingReadService reads, IPermissionContext perms, ICurrentUserContext ctx, IClinicalRepository clinical)
    : IQueryHandler<GetBookingQuery, BookingListItemDto>
{
    public async Task<BookingListItemDto> Handle(GetBookingQuery q, CancellationToken ct)
    {
        var doctorFilter = await BookingReadScope.ResolveDoctorFilterAsync(perms, ctx, clinical, q.TenantId, ct);
        var item = await reads.GetItemAsync(q.TenantId, q.BookingId, ct)
            ?? throw new KeyNotFoundException("Booking not found.");
        // Self-scoped caller: a booking belonging to another doctor is out of scope → 404 (no existence leak).
        if (doctorFilter is { } docId && item.DoctorId != docId)
            throw new KeyNotFoundException("Booking not found.");
        return item;
    }
}

// ---- Booking no-show risk (AI sibling-service advisory) ------------------------------------------

// ISelfManagedTransaction: this handler performs an EXTERNAL HTTP call (the AI service), which must NOT run
// inside the TenantScopeQueryBehavior read transaction (it would pin a pooled DB connection across the network
// call — a pool-drain hazard). So it opens its own tenant scope JUST for the DB feature read, disposes it, then
// calls the AI service with no DB connection held (the auditor-required split; mirrors the payout/ABDM commands).
public sealed record GetBookingNoShowRiskQuery(Guid TenantId, Guid BookingId)
    : IQuery<NoShowRiskDto>, ISelfManagedTransaction;

public sealed class GetBookingNoShowRiskQueryHandler(IBookingReadService reads, IAiNoShowClient ai, IUnitOfWork uow)
    : IQueryHandler<GetBookingNoShowRiskQuery, NoShowRiskDto>
{
    public async Task<NoShowRiskDto> Handle(GetBookingNoShowRiskQuery q, CancellationToken ct)
    {
        // Phase 1 — read the (non-PHI) feature snapshot inside an own tenant scope (RLS), then DISPOSE the scope
        // so its DB connection is released before the network call. 404 if the booking isn't in this tenant.
        NoShowFeatures? features;
        await using (var scope = await uow.BeginTenantScopeAsync(q.TenantId, ct))
        {
            features = await reads.GetNoShowFeaturesAsync(q.TenantId, q.BookingId, ct);
            // read-only — no commit; disposing rolls back the empty tx and clears SET LOCAL app.tenant_id.
        }
        if (features is null)
            throw new KeyNotFoundException("Booking not found.");

        // Phase 2 — call the AI service OUTSIDE any DB transaction. A null result means the AI service is
        // unavailable — surface that (Available=false), don't fabricate a score. serviceBearer is null on the
        // on-demand request path — the adapter forwards the caller's JWT (a worker would pass a service token).
        var risk = await ai.PredictAsync(q.BookingId, features, serviceBearer: null, ct);
        return risk is null
            ? new NoShowRiskDto(q.BookingId, Available: false, null, null, null, null)
            : new NoShowRiskDto(q.BookingId, Available: true, risk.Probability, risk.Band, risk.ModelName, risk.Source);
    }
}

public sealed record GetConversationQuery(Guid TenantId, Guid BookingId)
    : IQuery<IReadOnlyList<ConversationMessageDto>>;

public sealed class GetConversationQueryHandler(IBookingReadService reads)
    : IQueryHandler<GetConversationQuery, IReadOnlyList<ConversationMessageDto>>
{
    public Task<IReadOnlyList<ConversationMessageDto>> Handle(GetConversationQuery q, CancellationToken ct)
        => reads.GetConversationAsync(q.TenantId, q.BookingId, ct);
}

// ---- Doctors / slots -----------------------------------------------------------------------------

public sealed record ListDoctorsQuery(Guid TenantId) : IQuery<IReadOnlyList<DoctorDto>>;

public sealed class ListDoctorsQueryHandler(IDoctorReadService reads)
    : IQueryHandler<ListDoctorsQuery, IReadOnlyList<DoctorDto>>
{
    public Task<IReadOnlyList<DoctorDto>> Handle(ListDoctorsQuery q, CancellationToken ct)
        => reads.ListAsync(q.TenantId, ct);
}

public sealed record GetDoctorSlotsQuery(Guid TenantId, Guid DoctorId, DateOnly Date)
    : IQuery<IReadOnlyList<SlotDto>>;

public sealed class GetDoctorSlotsQueryHandler(IDoctorReadService reads)
    : IQueryHandler<GetDoctorSlotsQuery, IReadOnlyList<SlotDto>>
{
    public Task<IReadOnlyList<SlotDto>> Handle(GetDoctorSlotsQuery q, CancellationToken ct)
        => reads.GetSlotsAsync(q.TenantId, q.DoctorId, q.Date, ct);
}

// ---- Patients (masked list) ----------------------------------------------------------------------

public sealed record ListPatientsQuery(Guid TenantId, int Skip, int Take)
    : IQuery<IReadOnlyList<PatientListItemDto>>;

public sealed class ListPatientsQueryHandler(IPatientReadService reads)
    : IQueryHandler<ListPatientsQuery, IReadOnlyList<PatientListItemDto>>
{
    public Task<IReadOnlyList<PatientListItemDto>> Handle(ListPatientsQuery q, CancellationToken ct)
        => reads.ListAsync(q.TenantId, q.Skip, Math.Clamp(q.Take, 1, 200), ct);
}
