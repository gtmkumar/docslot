using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Application.Features.Docslot.Settings;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Tenant Settings read (<c>docslot.healthcare_facilities</c>, one row per tenant). The two jsonb columns are
/// pulled as <c>::text</c> and deserialized in-process so the typed DTO round-trips them faithfully and tolerates
/// missing keys (sparse jsonb → DTO defaults). The <c>whatsapp_access_token</c> column is DELIBERATELY never
/// named in the SELECT — the secret cannot leak into a response. Tenant-scoped (WHERE tenant_id = @p0).
/// </summary>
public sealed class SettingsReadService(PlatformDbContext db) : ISettingsReadService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SettingsDto?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<FacilitySettingsRow>(
                """
                SELECT
                    f.facility_type                AS "FacilityType",
                    f.specialty_focus              AS "SpecialtyFocus",
                    f.whatsapp_business_phone_id   AS "WhatsAppPhoneId",
                    f.whatsapp_verified_at         AS "WhatsAppVerifiedAt",
                    f.hfr_id                       AS "HfrId",
                    f.hfr_status                   AS "HfrStatus",
                    f.business_hours::text         AS "BusinessHoursJson",
                    f.appointment_settings::text   AS "AppointmentSettingsJson"
                FROM docslot.healthcare_facilities f
                WHERE f.tenant_id = @p0
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", tenantId))
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new SettingsDto(
            FacilityType: row.FacilityType,
            SpecialtyFocus: row.SpecialtyFocus,
            BusinessHours: DeserializeBusinessHours(row.BusinessHoursJson),
            AppointmentSettings: DeserializeAppointmentSettings(row.AppointmentSettingsJson),
            WhatsApp: new WhatsAppStatusDto(
                Connected: row.WhatsAppVerifiedAt is not null,
                PhoneNumberId: row.WhatsAppPhoneId,
                VerifiedAt: row.WhatsAppVerifiedAt),
            Hfr: new HfrStatusDto(row.HfrId, row.HfrStatus));
    }

    private static IReadOnlyDictionary<string, DayHours> DeserializeBusinessHours(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new Dictionary<string, DayHours>();
        return JsonSerializer.Deserialize<Dictionary<string, DayHours>>(json, JsonOptions)
               ?? new Dictionary<string, DayHours>();
    }

    private static AppointmentSettingsDto DeserializeAppointmentSettings(string? json)
    {
        // Missing-key tolerance: a sparse/empty jsonb deserializes onto the record's positional defaults.
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new AppointmentSettingsDto();
        return JsonSerializer.Deserialize<AppointmentSettingsDto>(json, JsonOptions) ?? new AppointmentSettingsDto();
    }

    private sealed record FacilitySettingsRow(
        string FacilityType,
        string? SpecialtyFocus,
        string? WhatsAppPhoneId,
        DateTimeOffset? WhatsAppVerifiedAt,
        string? HfrId,
        string? HfrStatus,
        string? BusinessHoursJson,
        string? AppointmentSettingsJson);
}

/// <summary>
/// Write-side facility settings (<c>docslot.healthcare_facilities</c>). The UPDATE runs through the SAME
/// <see cref="PlatformDbContext"/> the command's UnitOfWork transaction owns — the <c>SET LOCAL app.tenant_id</c>
/// applies (RLS) and tenant_id is the caller's claim, never a header. Only the two editable jsonb columns +
/// <c>updated_at</c> are touched; a null section is left at its existing value so a partial PATCH leaves the other
/// section intact. <c>business_hours</c> is merged per day via the jsonb <c>||</c> operator — supplying a subset of
/// days (e.g. just <c>sat</c>) overrides those days and preserves the rest; <c>appointment_settings</c> is a flat
/// settings object replaced wholesale. The token / hfr / facility_type columns are never written here.
/// </summary>
public sealed class SettingsRepository(PlatformDbContext db) : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> UpdateSettingsAsync(
        Guid tenantId,
        IReadOnlyDictionary<string, DayHours>? businessHours,
        AppointmentSettingsDto? appointmentSettings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // A null param leaves that jsonb column untouched. Supplied sections are serialized and cast to jsonb.
        // business_hours is shallow-merged (existing || supplied) so a partial PATCH overrides only the supplied
        // days; appointment_settings is replaced wholesale. updated_at is set explicitly (the trigger would also
        // set it, but we keep it deterministic for the clock-driven handler).
        var businessHoursJson = businessHours is null ? null : JsonSerializer.Serialize(businessHours, JsonOptions);
        var appointmentJson = appointmentSettings is null ? null : JsonSerializer.Serialize(appointmentSettings, JsonOptions);

        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.healthcare_facilities
            SET business_hours      = COALESCE(business_hours, jsonb_build_object()) || COALESCE(@p_business::jsonb, jsonb_build_object()),
                appointment_settings = COALESCE(@p_appointment::jsonb, appointment_settings),
                updated_at          = @p_now
            WHERE tenant_id = @p_tenant
            """,
            new NpgsqlParameter("@p_business", (object?)businessHoursJson ?? DBNull.Value),
            new NpgsqlParameter("@p_appointment", (object?)appointmentJson ?? DBNull.Value),
            new NpgsqlParameter("@p_now", nowUtc),
            new NpgsqlParameter("@p_tenant", tenantId));

        return affected > 0;
    }
}
