using System.Text.Json;
using mediq.Application.Abstractions;

namespace mediq.Application.Features.Docslot.Clinical;

/// <summary>
/// Generates <c>docslot.drug_alerts</c> when a prescription is issued/amended: parses the (plaintext, in-hand)
/// medication list, reads + decrypts the patient's active allergies and current medications, evaluates them
/// against the configured <see cref="IDrugInteractionSource"/>, and persists the resulting alerts — all INSIDE
/// the caller's unit-of-work transaction (alerts are atomic with the prescription).
///
/// PHI handling: the allergy/medication substance names are decrypted IN-PROCESS only to run the safety rules;
/// they are NEVER returned to the caller and are NOT copied verbatim into the (plaintext) alert columns — an
/// alert names the just-prescribed drug and links the encrypted source record via <c>conflicting_record_id</c>.
/// The decryption is logged as a purpose-of-use ('drug_safety_screening'). Screening is an internal SAFETY
/// control on the prescriber's own action: it is deliberately NOT blocked by the data-sharing consent gate,
/// because withholding an allergy/interaction check would endanger the patient (the auditor signed off on this).
/// </summary>
public sealed class DrugSafetyScreeningService(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IDrugInteractionSource source,
    IPurposeOfUseWriter purpose,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : IDrugSafetyScreeningService
{
    public async Task<int> ScreenPrescriptionAsync(
        Guid prescriptionId, Guid patientId, Guid tenantId, string medicationsJson, CancellationToken ct)
    {
        var prescribed = ParseMedications(medicationsJson);
        if (prescribed.Count == 0) return 0;   // nothing to screen (defensive — never throws on bad JSON)

        // Read the patient's active allergies + current medications (ciphertext), within the command tx.
        var history = await clinical.ListSafetyHistoryAsync(tenantId, patientId, ct);

        var allergies = new List<PatientAllergy>();
        var currentMeds = new List<PatientMedication>();
        if (history.Count > 0)
        {
            var userId = ctx.UserId;
            var encCtx = new EncryptionContext(userId, tenantId, "drug_safety_screening", patientId, ctx.IpAddress);
            foreach (var h in history)
            {
                // title holds the substance (allergy) / drug name (medication); decrypt only what the rules need.
                var name = await encryption.DecryptAsync(ClinicalFields.HistoryTitle, h.TitleEnc, encCtx, ct);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(h.RecordType, "allergy", StringComparison.Ordinal))
                    allergies.Add(new PatientAllergy(h.HistoryId, name, h.Severity));
                else if (string.Equals(h.RecordType, "medication", StringComparison.Ordinal))
                    currentMeds.Add(new PatientMedication(h.HistoryId, name));
            }

            // The decrypt above is a PHI read → record it in the purpose-of-use ledger (DPDP), not break-glass.
            // Declared purpose 'treatment' (drug-safety screening is part of treating the encounter — and the
            // allowed declared_purpose vocabulary); the automated provenance is carried in the notes.
            // NOTE (intentional, do not "fix"): the purpose-of-use + the 'drug_safety_screen' audit are written on
            // DEDICATED connections that commit independently of the issue/amend tx, and the purpose is recorded
            // BEFORE EvaluateAsync. So the access record (tamper-evidence) DURABLY outlives a later business
            // rollback — the in-process decrypt genuinely happened and must be logged even if issuance fails. The
            // generated drug_alerts, by contrast, are transactional with the prescription (atomic, may roll back).
            if (userId is Guid uid)
                await purpose.RecordAsync(new PurposeOfUseEntry(
                    uid, tenantId, "medical_history", patientId, "treatment", "automated drug-safety screening", false, null), ct);
        }

        var findings = await source.EvaluateAsync(prescribed, new PatientSafetyContext(allergies, currentMeds), ct);

        var written = 0;
        if (findings.Count > 0)
        {
            var now = clock.UtcNow;
            var alerts = findings.Select(f => mediq.Domain.Docslot.DrugAlert.Create(
                prescriptionId, patientId, f.Type.ToDbToken(), f.Severity.ToDbToken(),
                Trim200(f.MedicationName), f.ConflictingRecordId, f.Description, now)).ToList();
            written = await clinical.AddDrugAlertsAsync(alerts, ct);
        }

        // Audit the screen itself — count + source provenance only, NO PHI (the alerts carry the detail).
        await audit.RecordAsync(new AuditEntry(
            "drug_safety_screen", "prescription", prescriptionId, null, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Drug-safety screen: {written} alert(s) generated [{source.SourceName}]",
            Purpose: "treatment", LegalBasis: "consent"), ct);

        return written;
    }

    /// <summary>Tolerant parse of the medications JSON array. Handles BOTH shapes: the legacy
    /// <c>[{name,dose,frequency,duration}, ...]</c> (all strings) and the structured composer shape
    /// <c>[{name,strength,form,dose:{morning,noon,night},sos,weekly,timing,durationDays,instructions}, ...]</c>.
    /// Tolerance is PER ITEM (one odd line never suppresses screening of the rest) — a typed deserialize
    /// here once made the whole screen silently no-op when the structured dose OBJECT appeared (dose was
    /// typed string → JsonException → empty list → zero alerts), a clinical-safety failure mode. Only the
    /// drug NAME matters to the rules; dose/frequency/duration are carried for display only.</summary>
    private static IReadOnlyList<MedicationInput> ParseMedications(string medicationsJson)
    {
        if (string.IsNullOrWhiteSpace(medicationsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(medicationsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var list = new List<MedicationInput>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = Str(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                string? dose = null, frequency = null;
                if (el.TryGetProperty("dose", out var d))
                {
                    if (d.ValueKind == JsonValueKind.String) dose = d.GetString();                 // legacy
                    else if (d.ValueKind == JsonValueKind.Object)                                  // structured
                        frequency = $"{Num(d, "morning")}-{Num(d, "noon")}-{Num(d, "night")}";
                }
                dose ??= Str(el, "strength");
                frequency ??= Str(el, "frequency");
                var duration = Str(el, "duration")
                    ?? (el.TryGetProperty("durationDays", out var dd) && dd.ValueKind == JsonValueKind.Number
                        ? $"{dd.GetInt32()} days" : null);

                list.Add(new MedicationInput(name.Trim(), dose, frequency, duration));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Num(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Trim200(string s) => s.Length <= 200 ? s : s[..200];
}
