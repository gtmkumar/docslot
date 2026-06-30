using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;

namespace mediq.Application.Features.Docslot.Queries;

// ---- Dashboard summary ---------------------------------------------------------------------------

public sealed record GetDashboardSummaryQuery(Guid TenantId) : IQuery<DashboardSummaryDto>;

public sealed class GetDashboardSummaryQueryHandler(IBookingReadService reads)
    : IQueryHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    public Task<DashboardSummaryDto> Handle(GetDashboardSummaryQuery q, CancellationToken ct)
        => reads.GetSummaryAsync(q.TenantId, ct);
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

public sealed class ListBookingsQueryHandler(IBookingReadService reads)
    : IQueryHandler<ListBookingsQuery, (IReadOnlyList<BookingListItemDto> Items, int Total)>
{
    public Task<(IReadOnlyList<BookingListItemDto> Items, int Total)> Handle(ListBookingsQuery q, CancellationToken ct)
        => reads.ListAsync(q.Filter, ct);
}

public sealed record GetBookingQuery(Guid TenantId, Guid BookingId) : IQuery<BookingListItemDto>;

public sealed class GetBookingQueryHandler(IBookingReadService reads)
    : IQueryHandler<GetBookingQuery, BookingListItemDto>
{
    public async Task<BookingListItemDto> Handle(GetBookingQuery q, CancellationToken ct)
        => await reads.GetItemAsync(q.TenantId, q.BookingId, ct)
           ?? throw new KeyNotFoundException("Booking not found.");
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
