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
