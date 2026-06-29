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
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

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

    /// <summary>Tolerant parse of the medications JSON array (<c>[{name,dose,frequency,duration}, ...]</c>).
    /// Returns an empty list on malformed input — screening must never break prescription issuance.</summary>
    private static IReadOnlyList<MedicationInput> ParseMedications(string medicationsJson)
    {
        if (string.IsNullOrWhiteSpace(medicationsJson)) return [];
        try
        {
            var lines = JsonSerializer.Deserialize<List<MedLine>>(medicationsJson, Json);
            if (lines is null) return [];
            return lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Name))
                .Select(l => new MedicationInput(l.Name!.Trim(), l.Dose, l.Frequency, l.Duration))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Trim200(string s) => s.Length <= 200 ? s : s[..200];

    private sealed record MedLine(string? Name, string? Dose, string? Frequency, string? Duration);
}
