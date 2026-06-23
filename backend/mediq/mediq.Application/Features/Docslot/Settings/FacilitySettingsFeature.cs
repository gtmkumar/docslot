using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;

namespace mediq.Application.Features.Docslot.Settings;

// =====================================================================================================
// Tenant Settings (docslot.healthcare_facilities — one row per tenant). READ + PATCH vertical slice.
//
// The facility row is tenant-scoped (UNIQUE tenant_id). The settings surface exposes ONLY the operational
// config a tenant owner may see/edit: facility identity (read-only here), business_hours + appointment_settings
// (the two editable jsonb columns), and the WhatsApp / HFR *connection status* (read-only, and the
// whatsapp_access_token SECRET is NEVER projected into the DTO). PATCH may touch business_hours and/or
// appointment_settings ONLY — token / hfr / facility_type are refused (not part of the request shape).
//
// tenant_id always comes from ICurrentUserContext (the JWT claim), never a header. The PATCH runs inside the
// command's tenant-scoped UnitOfWork transaction (SET LOCAL app.tenant_id) so RLS applies. Per the house style
// the Request/Result/DTO records are co-located here (only enums/cross-cutting DTOs live in SharedDataModel).
// =====================================================================================================

// ---- DTO (shared by the GET query result and the PATCH command result) ---------------------------

/// <summary>
/// The tenant Settings read-model. Round-trips business_hours + appointment_settings faithfully and surfaces
/// the WhatsApp / HFR connection status. NEVER carries the whatsapp_access_token (secret).
/// </summary>
public sealed record SettingsDto(
    string FacilityType,
    string? SpecialtyFocus,
    IReadOnlyDictionary<string, DayHours> BusinessHours,
    AppointmentSettingsDto AppointmentSettings,
    WhatsAppStatusDto WhatsApp,
    HfrStatusDto Hfr);

/// <summary>Per-day open/close window. <c>Open</c>/<c>Close</c> are "HH:mm" strings (null when closed).</summary>
public sealed record DayHours(string? Open, string? Close, bool Closed);

/// <summary>
/// Typed appointment rules projected from the appointment_settings jsonb. Missing keys tolerate to sensible
/// defaults (matches the seeded shape) so a sparse/empty jsonb never throws.
/// </summary>
public sealed record AppointmentSettingsDto(
    int SlotDurationMinutes = 15,
    int BookingCutoffHours = 2,
    bool AutoConfirm = true,
    int MaxAdvanceDays = 30,
    bool AllowOverbooking = false,
    int ReminderHoursBefore = 24,
    int NoShowGraceMinutes = 15);

/// <summary>WhatsApp connection status — token deliberately absent. <c>Connected</c> = verified_at IS NOT NULL.</summary>
public sealed record WhatsAppStatusDto(bool Connected, string? PhoneNumberId, DateTimeOffset? VerifiedAt);

/// <summary>Health Facility Registry (HFR) status — read-only here.</summary>
public sealed record HfrStatusDto(string? Id, string? Status);

// ---- GET /api/v1/settings ------------------------------------------------------------------------

public sealed record GetSettingsQuery(Guid TenantId) : IQuery<SettingsDto>;

public sealed class GetSettingsQueryHandler(ISettingsReadService reads)
    : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<SettingsDto> Handle(GetSettingsQuery q, CancellationToken ct)
        => await reads.GetAsync(q.TenantId, ct)
           ?? throw new KeyNotFoundException("No facility settings exist for this tenant.");
}

// ---- PATCH /api/v1/settings ----------------------------------------------------------------------

/// <summary>
/// Partial update of the tenant's facility settings. The request may include <c>BusinessHours</c> and/or
/// <c>AppointmentSettings</c>; a null section is left untouched (true PATCH semantics). Whatsapp token, HFR and
/// facility_type are intentionally NOT part of the request — they cannot be changed through this endpoint.
/// </summary>
public sealed record UpdateSettingsCommand(Guid TenantId, UpdateSettingsRequest Request) : ICommand<SettingsDto>;

public sealed record UpdateSettingsRequest(
    IReadOnlyDictionary<string, DayHours>? BusinessHours = null,
    AppointmentSettingsDto? AppointmentSettings = null);

public sealed class UpdateSettingsValidator : AbstractValidator<UpdateSettingsCommand>
{
    // The seven canonical lower-case day keys. A supplied business_hours map must use these keys only.
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public UpdateSettingsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request)
            .Must(r => r.BusinessHours is not null || r.AppointmentSettings is not null)
            .WithMessage("Provide businessHours and/or appointmentSettings — at least one section is required.");

        When(x => x.Request.AppointmentSettings is not null, () =>
        {
            RuleFor(x => x.Request.AppointmentSettings!.SlotDurationMinutes)
                .InclusiveBetween(5, 120).WithMessage("slotDurationMinutes must be between 5 and 120.");
            RuleFor(x => x.Request.AppointmentSettings!.BookingCutoffHours)
                .GreaterThanOrEqualTo(0).WithMessage("bookingCutoffHours must be non-negative.");
            RuleFor(x => x.Request.AppointmentSettings!.MaxAdvanceDays)
                .GreaterThan(0).WithMessage("maxAdvanceDays must be greater than 0.");
            RuleFor(x => x.Request.AppointmentSettings!.ReminderHoursBefore)
                .GreaterThanOrEqualTo(0).WithMessage("reminderHoursBefore must be non-negative.");
            RuleFor(x => x.Request.AppointmentSettings!.NoShowGraceMinutes)
                .InclusiveBetween(0, 240).WithMessage("noShowGraceMinutes must be between 0 and 240.");
        });

        When(x => x.Request.BusinessHours is not null, () =>
            RuleFor(x => x.Request.BusinessHours!)
                .Must(BeAValidWeek)
                .WithMessage("businessHours keys must be a subset of mon..sun; open/close must be HH:mm and open < close when not closed."));
    }

    private static bool BeAValidWeek(IReadOnlyDictionary<string, DayHours> week)
    {
        foreach (var (key, day) in week)
        {
            if (!DayKeys.Contains(key)) return false;
            if (day.Closed) continue;                                  // closed day: open/close may be null
            if (!TryParse(day.Open, out var open) || !TryParse(day.Close, out var close)) return false;
            if (open >= close) return false;                           // open must precede close
        }
        return true;
    }

    private static bool TryParse(string? value, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParseExact(value, "HH:mm", out time);
    }
}

public sealed class UpdateSettingsCommandHandler(
    ISettingsRepository facilities, ISettingsReadService reads, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<SettingsDto> Handle(UpdateSettingsCommand command, CancellationToken ct)
    {
        // The PATCH only mutates the editable jsonb columns; a missing facility row is a 404 (consistent with GET).
        var updated = await facilities.UpdateSettingsAsync(
            command.TenantId, command.Request.BusinessHours, command.Request.AppointmentSettings, clock.UtcNow, ct);
        if (!updated)
            throw new KeyNotFoundException("No facility settings exist for this tenant.");

        var sections = new List<string>();
        if (command.Request.BusinessHours is not null) sections.Add("business_hours");
        if (command.Request.AppointmentSettings is not null) sections.Add("appointment_settings");

        await audit.RecordAsync(new AuditEntry(
            "update", "facility_settings", command.TenantId, "Tenant settings", ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Updated {string.Join(", ", sections)}"), ct);

        // Re-project the row so the response reflects the persisted state (still within the tenant-scoped tx).
        return await reads.GetAsync(command.TenantId, ct)
               ?? throw new KeyNotFoundException("No facility settings exist for this tenant.");
    }
}
