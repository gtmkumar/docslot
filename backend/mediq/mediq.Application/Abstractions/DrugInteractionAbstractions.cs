namespace mediq.Application.Abstractions;

/// <summary>
/// A drug-safety knowledge source: given the medications being prescribed and the patient's safety context
/// (recorded allergies + current medications), it returns the clinical-safety findings to raise as
/// <c>docslot.drug_alerts</c>. This is the seam behind which a real, licensed interaction database
/// (First Databank / Medi-Span / RxNorm+DDInter) is swapped in for production — the dev default is a small,
/// curated, deliberately-labelled subset of well-established rules (NOT a substitute for a licensed source).
/// The contract is a PURE evaluation (no I/O, no DB, no PHI persistence) — the caller owns reading the
/// encrypted history, decrypting it in-process, and persisting any resulting alerts inside its transaction.
/// </summary>
public interface IDrugInteractionSource
{
    /// <summary>Provenance tag recorded on generated alerts/audit (e.g. <c>curated-dev-v1</c>, <c>fdb-2026.1</c>).</summary>
    string SourceName { get; }

    /// <summary>
    /// Screens <paramref name="prescribed"/> against itself and the patient's <paramref name="context"/>
    /// (allergies + current medications). Returns zero or more findings; an empty list means "no rule fired"
    /// — NOT a positive "all clear" assurance (a richer source may find more). Must never throw on ordinary
    /// input (unknown drug names are simply un-matched).
    /// </summary>
    Task<IReadOnlyList<DrugAlertFinding>> EvaluateAsync(
        IReadOnlyList<MedicationInput> prescribed, PatientSafetyContext context, CancellationToken ct);
}

/// <summary>One medication line from a prescription (the <c>{name,dose,frequency,duration}</c> shape).</summary>
public sealed record MedicationInput(string Name, string? Dose = null, string? Frequency = null, string? Duration = null);

/// <summary>
/// The patient's decrypted safety context for screening. <paramref name="Allergies"/> are the free-text
/// allergen substances recorded in medical history (record_type='allergy'); <paramref name="CurrentMedications"/>
/// are active medication-history entries (record_type='medication'). Each carries the source history record id
/// so an alert can point at it via <c>conflicting_record_id</c> WITHOUT copying the encrypted free-text into a
/// plaintext alert column.
/// </summary>
public sealed record PatientSafetyContext(
    IReadOnlyList<PatientAllergy> Allergies,
    IReadOnlyList<PatientMedication> CurrentMedications);

/// <summary>A decrypted allergy substance + its severity + the (encrypted) history record it came from.</summary>
public sealed record PatientAllergy(Guid HistoryId, string Substance, string? Severity);

/// <summary>A decrypted active current-medication name + the (encrypted) history record it came from.</summary>
public sealed record PatientMedication(Guid HistoryId, string Name);

/// <summary>
/// A safety finding to persist as a <c>docslot.drug_alerts</c> row. <paramref name="MedicationName"/> is the
/// just-prescribed drug that triggered the alert. <paramref name="ConflictingRecordId"/>, when set, links the
/// encrypted history record (allergy / current med) the conflict is with — the detail stays behind encryption.
/// </summary>
public sealed record DrugAlertFinding(
    DrugAlertType Type,
    DrugAlertSeverity Severity,
    string MedicationName,
    string Description,
    Guid? ConflictingRecordId = null);

/// <summary>Maps 1:1 to the <c>drug_alerts.alert_type</c> CHECK domain.</summary>
public enum DrugAlertType { Allergy, Interaction, Contraindication, Duplicate, PregnancyWarning, Dosage }

/// <summary>Maps 1:1 to the <c>drug_alerts.severity</c> CHECK domain.</summary>
public enum DrugAlertSeverity { Low, Moderate, High, Critical }

/// <summary>Lowercase DB tokens for the <c>drug_alerts</c> CHECK constraints (the enums above are app-facing).</summary>
public static class DrugAlertVocabulary
{
    public static string ToDbToken(this DrugAlertType t) => t switch
    {
        DrugAlertType.Allergy => "allergy",
        DrugAlertType.Interaction => "interaction",
        DrugAlertType.Contraindication => "contraindication",
        DrugAlertType.Duplicate => "duplicate",
        DrugAlertType.PregnancyWarning => "pregnancy_warning",
        DrugAlertType.Dosage => "dosage",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown drug-alert type."),
    };

    public static string ToDbToken(this DrugAlertSeverity s) => s switch
    {
        DrugAlertSeverity.Low => "low",
        DrugAlertSeverity.Moderate => "moderate",
        DrugAlertSeverity.High => "high",
        DrugAlertSeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unknown drug-alert severity."),
    };
}

/// <summary>
/// Orchestrates drug-safety screening when a prescription is issued/amended: parses the (plaintext, in-hand)
/// medication list, reads + decrypts the patient's allergies and current medications, evaluates them against
/// the configured <see cref="IDrugInteractionSource"/>, and persists the resulting <c>docslot.drug_alerts</c>
/// — all INSIDE the caller's unit-of-work transaction (so the alerts are atomic with the prescription).
/// </summary>
public interface IDrugSafetyScreeningService
{
    /// <summary>
    /// Screens a just-persisted prescription and writes any alerts. <paramref name="medicationsJson"/> is the
    /// plaintext medications array the caller already holds (it is NOT re-read/decrypted here). Returns the
    /// number of alerts written. Best-effort by contract: callers run it in-tx for atomicity.
    /// </summary>
    Task<int> ScreenPrescriptionAsync(
        Guid prescriptionId, Guid patientId, Guid tenantId, string medicationsJson, CancellationToken ct);
}
