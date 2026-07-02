using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Clinical;

// ---- Unified patient timeline (read-model: merged, permission-filtered, purpose + consent gated) -----

public sealed record GetPatientTimelineQuery(Guid TenantId, Guid PatientId, string DeclaredPurpose)
    : IQuery<PatientTimelineDto>;

/// <summary>
/// Builds the read-only unified patient timeline: a relationship strip (non-PHI, from bookings) plus a merged,
/// most-recent-first feed of prescriptions (non-draft), lab reports, vaccinations, and paper-import document
/// cards. TWO independent filters shape it: (1) the DPDP consent gate — one aggregate decision (active consent
/// OR a patient-wide break-glass grant for a readable resource type; else 403), recorded ONCE as a
/// 'patient_timeline' purpose-of-use entry; and (2) per-category READ permission
/// (<see cref="IPermissionContext"/>) — a category the caller cannot read is omitted from BOTH the chips and the
/// items (a permitted category is always charted, even at count 0). Titles/summaries are decrypted in-policy;
/// summaries carry only medication COUNTS, never drug names.
/// </summary>
public sealed class GetPatientTimelineQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IPatientRepository patients,
    IPurposeOfUseWriter purpose, IBreakGlassService breakGlass, IPermissionContext permissions, ICurrentUserContext ctx)
    : IQueryHandler<GetPatientTimelineQuery, PatientTimelineDto>
{
    private const int MaxItems = 200;

    public async Task<PatientTimelineDto> Handle(GetPatientTimelineQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read the patient timeline (DPDP)."] });

        // Patient must belong to the tenant (patient_tenant_links) → 404 (no strip/PHI leak for a foreign patient).
        if (!await patients.IsLinkedToTenantAsync(q.PatientId, q.TenantId, ct))
            throw new KeyNotFoundException("Patient not found.");

        var canReadPrescriptions = permissions.Has("docslot.prescription.read");
        var canReadReports = permissions.Has("docslot.report.read");
        var canReadHistory = permissions.Has("docslot.medical_history.read");

        // PER-CATEGORY consent gate: each category is unlocked by (active consent) OR a patient-wide break-glass
        // grant for THAT category's resource_type (prescription→"prescription", lab_report→"lab_report",
        // vaccination+document→"medical_history"). Break-glass grants are resource-type-scoped, so a grant for one
        // type must NEVER disclose another (a "prescription" grant does not reveal lab reports / vaccination titles /
        // external_doctor_name). A category that is neither consented nor grant-unlocked is OMITTED (chip + items),
        // exactly like the permission filter. Resolve each readable type's gate exactly once.
        var patient = await patients.GetByIdAsync(q.PatientId, ct);
        var consentActive = patient?.HasActiveConsent ?? false;

        async Task<(bool Allowed, BreakGlassGrant? Grant)> GateAsync(string resourceType)
        {
            if (consentActive) return (true, null);
            var g = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, q.PatientId, resourceType, null, ct);
            return (g is not null, g);
        }
        var prescriptionGate = canReadPrescriptions ? await GateAsync("prescription") : (Allowed: false, Grant: (BreakGlassGrant?)null);
        var labGate = canReadReports ? await GateAsync("lab_report") : (Allowed: false, Grant: (BreakGlassGrant?)null);
        var historyGate = canReadHistory ? await GateAsync("medical_history") : (Allowed: false, Grant: (BreakGlassGrant?)null);

        // Consent-denied with no unlocking grant for ANY readable category → refuse the whole request (mirrors the
        // sibling list reads' 403). A caller with NO readable category never reaches here (the endpoint's any-of
        // permission gate already 403'd), so this only fires when a category was permission-open but consent-closed.
        var anyPermitted = canReadPrescriptions || canReadReports || canReadHistory;
        var anyAccessible = prescriptionGate.Allowed || labGate.Allowed || historyGate.Allowed;
        if (anyPermitted && !anyAccessible)
            throw new ForbiddenException("Patient has no active consent; timeline read refused (DPDP).");

        // One purpose-of-use entry per ACCESSED resource type; the break-glass flag reflects THAT type's grant only
        // (never a blanket entry attributing one grant's justification to another category's PHI).
        Task RecordPurposeAsync(string resourceType, BreakGlassGrant? grant) =>
            purpose.RecordAsync(new PurposeOfUseEntry(
                userId, q.TenantId, resourceType, q.PatientId, q.DeclaredPurpose, "patient timeline",
                grant is not null, grant?.Justification), ct);

        var items = new List<TimelineItemDto>();
        var categories = new List<TimelineCategoryDto>();

        // ── Prescriptions (non-draft) ─────────────────────────────────────────────────────────────
        if (canReadPrescriptions && prescriptionGate.Allowed)
        {
            var encCtx = new EncryptionContext(userId, q.TenantId, "prescription", q.PatientId, ctx.IpAddress);
            var rows = await clinical.ListTimelinePrescriptionsAsync(q.TenantId, q.PatientId, ct);
            foreach (var r in rows)
            {
                var title = string.IsNullOrEmpty(r.DiagnosisEnc)
                    ? "Prescription"
                    : await encryption.DecryptAsync(PrescriptionFields.Diagnosis, r.DiagnosisEnc, encCtx, ct);
                var medsJson = await encryption.DecryptAsync(PrescriptionFields.Medications, r.MedicationsEnc, encCtx, ct);
                var medCount = CountMedications(medsJson);   // COUNT only — never drug names
                // doctors.full_name/display_name already carry the "Dr." honorific — don't prepend another.
                var subtitle = r.DoctorName is null ? null
                    : string.IsNullOrWhiteSpace(r.Specialization) ? r.DoctorName : $"{r.DoctorName} · {r.Specialization}";
                var tags = new List<string>();
                if (!string.IsNullOrEmpty(r.PrescriptionNumber)) tags.Add(r.PrescriptionNumber!);
                tags.Add(r.Status);
                items.Add(new TimelineItemDto(r.PrescriptionId, "prescription", Utc(r.FinalizedAt ?? r.CreatedAt),
                    title, subtitle, $"{medCount} {Plural(medCount, "medicine")}", tags, Unverified: false,
                    HasAttachment: false, new TimelineRefDto("prescription", r.PrescriptionId)));
            }
            categories.Add(new TimelineCategoryDto("prescription", "Prescriptions", "नुस्खे", rows.Count));
            await RecordPurposeAsync("prescription", prescriptionGate.Grant);
        }

        // ── Lab reports (headers — test name is plaintext catalog data, not PHI) ────────────────────
        if (canReadReports && labGate.Allowed)
        {
            var rows = await clinical.ListTimelineLabReportsAsync(q.TenantId, q.PatientId, ct);
            foreach (var r in rows)
            {
                var tags = new List<string>();
                if (!string.IsNullOrEmpty(r.ReportNumber)) tags.Add(r.ReportNumber!);
                tags.Add(r.Status);
                if (r.HasCriticalFindings) tags.Add("critical");
                items.Add(new TimelineItemDto(r.ReportId, "lab_report", Utc(r.CreatedAt),
                    r.TestName, Subtitle: null, TitleCase(r.Status), tags, Unverified: false,
                    r.HasFile, new TimelineRefDto("lab_report", r.ReportId)));
            }
            categories.Add(new TimelineCategoryDto("lab_report", "Lab Reports", "लैब रिपोर्ट", rows.Count));
            await RecordPurposeAsync("lab_report", labGate.Grant);
        }

        // ── Vaccinations + document cards (both derived from medical_history) ────────────────────────
        if (canReadHistory && historyGate.Allowed)
        {
            var encCtx = new EncryptionContext(userId, q.TenantId, "medical_history", q.PatientId, ctx.IpAddress);
            var history = await clinical.ListMedicalHistoryAsync(q.TenantId, q.PatientId, ct);

            // Vaccination entries — decrypted title, dated by when the vaccine was given.
            var vaccinations = history.Where(h => string.Equals(h.RecordType, "vaccination", StringComparison.Ordinal)).ToList();
            foreach (var h in vaccinations)
            {
                var title = await encryption.DecryptAsync(ClinicalFields.HistoryTitle, h.TitleEnc, encCtx, ct);
                var external = !string.Equals(h.Source, "clinic", StringComparison.Ordinal);
                string? subtitle = null;
                if (external)
                {
                    var doctor = string.IsNullOrEmpty(h.ExternalDoctorNameEnc)
                        ? null
                        : await encryption.DecryptAsync(ClinicalFields.HistoryExternalDoctorName, h.ExternalDoctorNameEnc, encCtx, ct);
                    subtitle = string.IsNullOrWhiteSpace(doctor) ? "Imported" : $"Imported · {doctor}";
                }
                var tags = new List<string>();
                if (!string.IsNullOrEmpty(h.Severity)) tags.Add(h.Severity!);
                items.Add(new TimelineItemDto(h.HistoryId, "vaccination",
                    h.StartedDate is { } sd ? Utc(sd) : Utc(h.AddedAt),
                    title, subtitle, Summary: null, tags,
                    external && h.VerifiedAt is null, !string.IsNullOrEmpty(h.AttachmentUrl),
                    new TimelineRefDto("medical_history", h.HistoryId)));
            }
            categories.Add(new TimelineCategoryDto("vaccination", "Vaccinations", "टीकाकरण", vaccinations.Count));

            // Document cards — one per paper-import batch (grouped by import_batch_id). All rows of a batch share
            // the external prescriber / recorded date / attachment, so read those off the first row.
            var batches = history.Where(h => h.ImportBatchId is not null)
                .GroupBy(h => h.ImportBatchId!.Value)
                .ToList();
            foreach (var batch in batches)
            {
                var rows = batch.ToList();
                var first = rows[0];
                var doctor = string.IsNullOrEmpty(first.ExternalDoctorNameEnc)
                    ? null
                    : await encryption.DecryptAsync(ClinicalFields.HistoryExternalDoctorName, first.ExternalDoctorNameEnc, encCtx, ct);
                var subtitle = string.IsNullOrWhiteSpace(doctor) ? "Imported" : $"Imported · {doctor}";
                var medCount = rows.Count(r => string.Equals(r.RecordType, "medication", StringComparison.Ordinal));
                var anyUnverified = rows.Any(r => !string.Equals(r.Source, "clinic", StringComparison.Ordinal) && r.VerifiedAt is null);
                var occurredAt = first.RecordedDate is { } rd ? Utc(rd) : Utc(rows.Max(r => r.AddedAt));
                items.Add(new TimelineItemDto(batch.Key, "document", occurredAt,
                    SourceTitle(first.Source), subtitle,
                    $"{rows.Count} {Plural(rows.Count, "record")} · {medCount} {Plural(medCount, "medication")}",
                    Tags: [], anyUnverified, !string.IsNullOrEmpty(first.AttachmentUrl),
                    new TimelineRefDto("medical_history_batch", batch.Key)));
            }
            categories.Add(new TimelineCategoryDto("document", "Documents", "दस्तावेज़", batches.Count));
            await RecordPurposeAsync("medical_history", historyGate.Grant);
        }

        var sorted = items.OrderByDescending(i => i.OccurredAt).Take(MaxItems).ToList();
        var strip = await clinical.GetPatientTimelineStripAsync(q.TenantId, q.PatientId, ct);
        return new PatientTimelineDto(new PatientTimelineStripDto(strip.PatientSince, strip.VisitCount), categories, sorted);
    }

    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
    private static DateTimeOffset Utc(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static string Plural(int n, string word) => n == 1 ? word : word + "s";
    private static string TitleCase(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string SourceTitle(string source) => source switch
    {
        "paper_prescription" => "Paper prescription",
        "patient_reported" => "Patient-reported records",
        _ => "Document",
    };

    /// <summary>Counts medication entries in a decrypted medications JSON array WITHOUT surfacing any drug name —
    /// tolerant of both the legacy and structured composer shapes (only the presence of a non-empty "name" matters).
    /// A malformed/empty payload counts as 0 (never throws, never leaks).</summary>
    private static int CountMedications(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
            var n = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nm.GetString()))
                    n++;
            return n;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
