namespace mediq.SharedDataModel.Docslot.Security;

/// <summary>
/// Single source of truth for how a raw <c>platform.audit_log</c> row is bucketed for the Audit tab's filter
/// rails. Derivations here are PURE and deterministic so the SQL page/facet queries and the row DTOs agree.
///
/// <para><b>Category</b> — maps <c>resource_type</c> into a fixed set of business buckets
/// (Bookings/Patients/Payments/Team/Settings/Security/Analytics), falling back to <c>Other</c> for anything
/// unmapped. The inverse (<see cref="ResourceTypesForCategory"/>) lets the read query push a category filter
/// down to SQL as a <c>resource_type = ANY(...)</c> predicate WITHOUT duplicating the mapping.</para>
///
/// <para><b>Severity</b> — a 3-level heuristic derived from <c>success</c> + whether the action is
/// "dangerous". "Dangerous" mirrors <c>platform.permissions.is_dangerous</c>: destructive / security-sensitive
/// verbs (delete, erase, break_glass, revoke, suspend, impersonate, refund, ...). The matrix is:
/// <list type="bullet">
///   <item><description><b>Critical</b> = a dangerous action that FAILED (e.g. a denied erase / delete).</description></item>
///   <item><description><b>Warning</b> = a non-dangerous failure OR a dangerous action that SUCCEEDED (noteworthy but expected).</description></item>
///   <item><description><b>Informational</b> = an ordinary successful, non-dangerous action.</description></item>
/// </list>
/// The row-level <see cref="Classify(bool,string?)"/> and the SQL facet fold
/// (<see cref="ClassifyByFlags(bool,bool)"/>) share the exact same rules.</para>
/// </summary>
public static class AuditTaxonomy
{
    public const string Bookings = "Bookings";
    public const string Patients = "Patients";
    public const string Payments = "Payments";
    public const string Team = "Team";
    public const string Settings = "Settings";
    public const string Security = "Security";
    public const string Analytics = "Analytics";
    public const string Other = "Other";

    /// <summary>The category buckets, in display order. <c>Other</c> is the open-ended fallback bucket.</summary>
    public static readonly IReadOnlyList<string> Categories =
        [Bookings, Patients, Payments, Team, Settings, Security, Analytics, Other];

    // resource_type (case-insensitive) -> category. Keep additions here (the only place) so both the SQL
    // filter push-down and the row projection stay in sync.
    private static readonly Dictionary<string, string> ResourceToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        // Bookings
        ["booking"] = Bookings, ["time_slot"] = Bookings, ["slot"] = Bookings, ["slot_hold"] = Bookings,
        ["opd_token"] = Bookings, ["appointment"] = Bookings, ["doctor_schedule"] = Bookings,
        // Patients (clinical) — resource_label only; no clinical body is ever exposed by the read.
        ["patient"] = Patients, ["prescription"] = Patients, ["lab_report"] = Patients,
        ["medical_history"] = Patients, ["abdm_health_record"] = Patients, ["consent"] = Patients,
        ["patient_consent"] = Patients, ["drug_alert"] = Patients,
        // Payments / money
        ["payout"] = Payments, ["commission"] = Payments, ["commission_ledger"] = Payments,
        ["invoice"] = Payments, ["billing"] = Payments, ["transaction"] = Payments,
        ["wallet"] = Payments, ["broker_wallet"] = Payments, ["refund"] = Payments,
        // Team / IAM
        ["user"] = Team, ["user_tenant_role"] = Team, ["role"] = Team, ["permission"] = Team,
        ["user_permission_override"] = Team, ["invitation"] = Team, ["broker"] = Team,
        // Settings / configuration
        ["tenant"] = Settings, ["tenant_settings"] = Settings, ["platform_settings"] = Settings,
        ["module_license"] = Settings, ["resource_type"] = Settings, ["product"] = Settings,
        ["webhook"] = Settings, ["api_client"] = Settings,
        // Security / compliance
        ["audit_anchor"] = Security, ["breach_log"] = Security, ["deletion_certificate"] = Security,
        ["data_export_request"] = Security, ["data_deletion_request"] = Security,
        ["break_glass"] = Security, ["break_glass_grant"] = Security, ["session"] = Security,
        ["user_session"] = Security, ["encryption_key"] = Security, ["anomaly"] = Security,
        ["login"] = Security, ["auth"] = Security, ["impersonation"] = Security,
        // Analytics / reporting
        ["report"] = Analytics, ["export"] = Analytics, ["data_export"] = Analytics,
        ["dashboard"] = Analytics, ["ai_extraction"] = Analytics, ["prediction"] = Analytics,
    };

    /// <summary>All <c>resource_type</c> values that map to a NAMED bucket (i.e. everything that is not Other).</summary>
    public static readonly IReadOnlyList<string> MappedResourceTypes = ResourceToCategory.Keys.ToArray();

    /// <summary>Bucket a raw <c>resource_type</c> into its category (<see cref="Other"/> when unmapped).</summary>
    public static string MapCategory(string? resourceType)
        => resourceType is not null && ResourceToCategory.TryGetValue(resourceType, out var c) ? c : Other;

    /// <summary>The resource_types that make up a named category (empty for <see cref="Other"/> — filter that inversely).</summary>
    public static string[] ResourceTypesForCategory(string category)
        => ResourceToCategory.Where(kv => kv.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
                             .Select(kv => kv.Key).ToArray();

    /// <summary>True if <paramref name="category"/> is one of the fixed named buckets (case-insensitive).</summary>
    public static bool IsKnownCategory(string category)
        => Categories.Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));

    public const string Informational = "Informational";
    public const string Warning = "Warning";
    public const string Critical = "Critical";

    public static readonly IReadOnlyList<string> Severities = [Informational, Warning, Critical];

    /// <summary>
    /// Audit action verbs treated as "dangerous" — mirrors <c>platform.permissions.is_dangerous</c>
    /// (destructive or security-sensitive operations). Used to raise the derived severity of the row.
    /// </summary>
    public static readonly IReadOnlySet<string> DangerousActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "erase", "destroy", "purge", "remove",
        "break_glass", "revoke", "revoke_permission", "grant_permission",
        "suspend", "deactivate", "impersonate", "refund",
        "report_breach", "anchor", "reset_access", "force_reset", "set_module_license",
    };

    /// <summary>True if the action verb is a dangerous (destructive / security-sensitive) one.</summary>
    public static bool IsDangerous(string? action)
        => action is not null && DangerousActions.Contains(action);

    /// <summary>Row-level severity from the raw success flag + action verb.</summary>
    public static string Classify(bool success, string? action)
        => ClassifyByFlags(success, IsDangerous(action));

    /// <summary>Severity from the two folded flags (used by the SQL facet aggregate, which groups on them).</summary>
    public static string ClassifyByFlags(bool success, bool dangerous)
    {
        if (!success) return dangerous ? Critical : Warning;
        return dangerous ? Warning : Informational;
    }

    /// <summary>Humanize a snake_case action verb into a display label, e.g. <c>break_glass</c> → "Break Glass".</summary>
    public static string Humanize(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "Unknown";
        var words = action.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
