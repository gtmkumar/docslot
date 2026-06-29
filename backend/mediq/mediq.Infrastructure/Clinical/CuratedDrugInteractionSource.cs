using System.Collections.Frozen;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Clinical;

/// <summary>
/// A curated, deliberately-small drug-safety knowledge source for development/demo. It encodes a subset of
/// WELL-ESTABLISHED, high-signal clinical rules (penicillin/sulfa/NSAID allergy cross-reactivity, a dozen
/// classic dangerous interaction pairs, and duplicate-therapy detection) with conservative defaults.
///
/// It is NOT a substitute for a licensed interaction database (First Databank / Medi-Span / RxNorm+DDInter):
/// it knows only the drugs/classes listed below, an empty result means "no curated rule fired" — never a
/// positive "all clear". Production swaps this adapter (same <see cref="IDrugInteractionSource"/> seam) for a
/// licensed source. Pure/deterministic: no I/O, no DB, no PHI persistence.
/// </summary>
public sealed class CuratedDrugInteractionSource : IDrugInteractionSource
{
    public string SourceName => "curated-dev-v1";

    // ---- Drug knowledge --------------------------------------------------------------------------
    // Brand / synonym → canonical generic (Indian-market brands included). Normalized (lower, single-spaced).
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["augmentin"] = "amoxicillin-clavulanate", ["clavam"] = "amoxicillin-clavulanate", ["co-amoxiclav"] = "amoxicillin-clavulanate",
        ["amoxyclav"] = "amoxicillin-clavulanate", ["mox"] = "amoxicillin", ["novamox"] = "amoxicillin",
        ["septran"] = "co-trimoxazole", ["bactrim"] = "co-trimoxazole", ["cotrimoxazole"] = "co-trimoxazole",
        ["septra"] = "co-trimoxazole", ["sulfamethoxazole-trimethoprim"] = "co-trimoxazole", ["tmp-smx"] = "co-trimoxazole",
        ["ecosprin"] = "aspirin", ["disprin"] = "aspirin", ["aspirin"] = "aspirin", ["asa"] = "aspirin", ["acetylsalicylic acid"] = "aspirin",
        ["brufen"] = "ibuprofen", ["combiflam"] = "ibuprofen", ["voveran"] = "diclofenac", ["voltaren"] = "diclofenac",
        ["zerodol"] = "aceclofenac", ["naprosyn"] = "naproxen",
        ["calpol"] = "paracetamol", ["crocin"] = "paracetamol", ["dolo"] = "paracetamol", ["tylenol"] = "paracetamol", ["acetaminophen"] = "paracetamol",
        ["ciplox"] = "ciprofloxacin", ["cifran"] = "ciprofloxacin",
        ["flagyl"] = "metronidazole", ["metrogyl"] = "metronidazole",
        ["clarithromycin"] = "clarithromycin", ["klacid"] = "clarithromycin", ["erythrocin"] = "erythromycin", ["azithral"] = "azithromycin", ["azee"] = "azithromycin",
        ["storvas"] = "atorvastatin", ["atorva"] = "atorvastatin", ["rosuvas"] = "rosuvastatin", ["zocor"] = "simvastatin",
        ["envas"] = "enalapril", ["zestril"] = "lisinopril", ["cardace"] = "ramipril",
        ["losar"] = "losartan", ["telma"] = "telmisartan",
        ["aldactone"] = "spironolactone",
        ["fludac"] = "fluoxetine", ["prozac"] = "fluoxetine", ["zoloft"] = "sertraline", ["nexito"] = "escitalopram", ["cipralex"] = "escitalopram",
        ["ultracet"] = "tramadol", ["tramazac"] = "tramadol",
        ["plavix"] = "clopidogrel", ["clopilet"] = "clopidogrel",
        ["pantop"] = "pantoprazole", ["omez"] = "omeprazole", ["ocid"] = "omeprazole",
        ["lanoxin"] = "digoxin", ["cordarone"] = "amiodarone", ["calaptin"] = "verapamil",
        ["zyloric"] = "allopurinol", ["imuran"] = "azathioprine",
        ["fluka"] = "fluconazole", ["forcan"] = "fluconazole", ["sporanox"] = "itraconazole",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // Canonical generic → therapeutic class tags used by the rules below.
    private static readonly FrozenDictionary<string, FrozenSet<string>> Classes = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        ["penicillin"] = C("penicillin", "betalactam"),
        ["amoxicillin"] = C("penicillin", "betalactam"),
        ["amoxicillin-clavulanate"] = C("penicillin", "betalactam"),
        ["ampicillin"] = C("penicillin", "betalactam"),
        ["cloxacillin"] = C("penicillin", "betalactam"),
        ["piperacillin"] = C("penicillin", "betalactam"),
        ["cephalexin"] = C("cephalosporin", "betalactam"),
        ["cefuroxime"] = C("cephalosporin", "betalactam"),
        ["ceftriaxone"] = C("cephalosporin", "betalactam"),
        ["cefixime"] = C("cephalosporin", "betalactam"),
        ["co-trimoxazole"] = C("sulfonamide", "trimethoprim"),
        ["trimethoprim"] = C("trimethoprim"),
        ["sulfasalazine"] = C("sulfonamide"),
        ["sulfadiazine"] = C("sulfonamide"),
        ["aspirin"] = C("salicylate", "nsaid", "antiplatelet"),
        ["ibuprofen"] = C("nsaid"),
        ["diclofenac"] = C("nsaid"),
        ["aceclofenac"] = C("nsaid"),
        ["naproxen"] = C("nsaid"),
        ["ketorolac"] = C("nsaid"),
        ["indomethacin"] = C("nsaid"),
        ["mefenamic acid"] = C("nsaid"),
        ["paracetamol"] = C("analgesic"),
        ["warfarin"] = C("anticoagulant"),
        ["acenocoumarol"] = C("anticoagulant"),
        ["clopidogrel"] = C("antiplatelet"),
        ["ciprofloxacin"] = C("fluoroquinolone"),
        ["levofloxacin"] = C("fluoroquinolone"),
        ["ofloxacin"] = C("fluoroquinolone"),
        ["metronidazole"] = C("nitroimidazole"),
        ["fluconazole"] = C("azole_antifungal"),
        ["itraconazole"] = C("azole_antifungal"),
        ["ketoconazole"] = C("azole_antifungal"),
        ["erythromycin"] = C("macrolide"),
        ["clarithromycin"] = C("macrolide"),
        ["azithromycin"] = C("macrolide"),
        ["atorvastatin"] = C("statin"),
        ["simvastatin"] = C("statin"),
        ["rosuvastatin"] = C("statin"),
        ["lovastatin"] = C("statin"),
        ["enalapril"] = C("ace_inhibitor"),
        ["lisinopril"] = C("ace_inhibitor"),
        ["ramipril"] = C("ace_inhibitor"),
        ["perindopril"] = C("ace_inhibitor"),
        ["captopril"] = C("ace_inhibitor"),
        ["losartan"] = C("arb"),
        ["telmisartan"] = C("arb"),
        ["valsartan"] = C("arb"),
        ["spironolactone"] = C("potassium_sparing"),
        ["amiloride"] = C("potassium_sparing"),
        ["triamterene"] = C("potassium_sparing"),
        ["fluoxetine"] = C("ssri"),
        ["sertraline"] = C("ssri"),
        ["escitalopram"] = C("ssri"),
        ["citalopram"] = C("ssri"),
        ["paroxetine"] = C("ssri"),
        ["tramadol"] = C("opioid", "serotonergic"),
        ["linezolid"] = C("maoi", "serotonergic"),
        ["selegiline"] = C("maoi", "serotonergic"),
        ["methotrexate"] = C("methotrexate"),
        ["digoxin"] = C("digoxin"),
        ["amiodarone"] = C("antiarrhythmic"),
        ["verapamil"] = C("ccb"),
        ["allopurinol"] = C("xanthine_oxidase_inhibitor"),
        ["azathioprine"] = C("thiopurine"),
        ["omeprazole"] = C("ppi"),
        ["pantoprazole"] = C("ppi"),
        ["potassium chloride"] = C("potassium"),
        ["potassium"] = C("potassium"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenSet<string> C(params string[] tags) => tags.ToFrozenSet(StringComparer.Ordinal);

    // Free-text allergy keyword → implied class(es). Lets "Penicillin allergy" / "Sulfa drug rash" resolve to
    // a cross-reactivity class even when no specific drug is named. Non-drug allergens (peanut, dust) match none.
    private static readonly (string Keyword, string[] Tags)[] AllergyKeywords =
    {
        ("penicillin", new[] { "penicillin", "betalactam" }),
        ("beta-lactam", new[] { "betalactam" }),
        ("betalactam", new[] { "betalactam" }),
        ("cephalosporin", new[] { "cephalosporin", "betalactam" }),
        ("cephalexin", new[] { "cephalosporin", "betalactam" }),
        ("sulfa", new[] { "sulfonamide" }),
        ("sulpha", new[] { "sulfonamide" }),
        ("sulfonamide", new[] { "sulfonamide" }),
        ("sulphonamide", new[] { "sulfonamide" }),
        ("nsaid", new[] { "nsaid" }),
        ("aspirin", new[] { "salicylate", "nsaid" }),
        ("salicylate", new[] { "salicylate" }),
        ("ibuprofen", new[] { "nsaid" }),
        ("diclofenac", new[] { "nsaid" }),
        ("macrolide", new[] { "macrolide" }),
        ("erythromycin", new[] { "macrolide" }),
    };

    // ---- Interaction rules (classic, high-signal) ------------------------------------------------
    // A rule fires when one drug matches A and a DIFFERENT drug matches B, with >=1 of them just-prescribed.
    private sealed record Rule(string[] A, string[] B, DrugAlertSeverity Severity, string Risk);

    private static readonly Rule[] InteractionRules =
    {
        new(["class:anticoagulant"], ["class:nsaid"], DrugAlertSeverity.High, "major bleeding risk"),
        new(["class:anticoagulant"], ["class:antiplatelet"], DrugAlertSeverity.High, "major bleeding risk"),
        new(["class:anticoagulant"], ["class:macrolide"], DrugAlertSeverity.High, "raises INR / bleeding risk"),
        new(["class:anticoagulant"], ["class:fluoroquinolone"], DrugAlertSeverity.High, "raises INR / bleeding risk"),
        new(["class:anticoagulant"], ["name:metronidazole"], DrugAlertSeverity.High, "raises INR / bleeding risk"),
        new(["class:anticoagulant"], ["class:azole_antifungal"], DrugAlertSeverity.High, "raises INR / bleeding risk"),
        new(["class:anticoagulant"], ["class:sulfonamide"], DrugAlertSeverity.High, "raises INR / bleeding risk"),
        new(["name:methotrexate"], ["class:trimethoprim"], DrugAlertSeverity.Critical, "additive antifolate — pancytopenia risk"),
        new(["name:methotrexate"], ["class:nsaid"], DrugAlertSeverity.High, "reduced clearance — methotrexate toxicity"),
        new(["class:ace_inhibitor", "class:arb"], ["class:potassium_sparing", "class:potassium"], DrugAlertSeverity.High, "hyperkalemia risk"),
        new(["class:ace_inhibitor"], ["class:arb"], DrugAlertSeverity.Moderate, "dual RAAS blockade — hyperkalemia / renal risk"),
        new(["class:statin"], ["class:macrolide", "class:azole_antifungal"], DrugAlertSeverity.High, "raised statin level — rhabdomyolysis risk"),
        new(["class:ssri", "class:serotonergic"], ["class:maoi"], DrugAlertSeverity.Critical, "serotonin syndrome risk"),
        new(["class:ssri"], ["name:tramadol"], DrugAlertSeverity.High, "serotonin syndrome / seizure risk"),
        new(["class:ssri"], ["class:nsaid", "class:anticoagulant"], DrugAlertSeverity.Moderate, "increased GI bleeding risk"),
        new(["name:clopidogrel"], ["name:omeprazole"], DrugAlertSeverity.Moderate, "reduced antiplatelet effect"),
        new(["name:digoxin"], ["name:amiodarone", "name:verapamil"], DrugAlertSeverity.High, "raised digoxin level — toxicity risk"),
        new(["name:allopurinol"], ["name:azathioprine"], DrugAlertSeverity.High, "marrow suppression risk"),
        new(["class:nsaid"], ["class:ace_inhibitor", "class:arb"], DrugAlertSeverity.Moderate, "reduced antihypertensive effect / renal risk"),
    };

    // Classes for which prescribing two members is a duplicate-therapy concern.
    private static readonly FrozenSet<string> DuplicateClasses =
        C("nsaid", "ace_inhibitor", "arb", "ssri", "statin", "ppi", "anticoagulant", "macrolide", "fluoroquinolone");

    public Task<IReadOnlyList<DrugAlertFinding>> EvaluateAsync(
        IReadOnlyList<MedicationInput> prescribed, PatientSafetyContext context, CancellationToken ct)
    {
        var findings = new List<DrugAlertFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // dedupe key

        void Add(DrugAlertFinding f)
        {
            var key = $"{f.Type}|{f.MedicationName.ToLowerInvariant()}|{f.ConflictingRecordId}|{f.Description}";
            if (seen.Add(key)) findings.Add(f);
        }

        // Build the screened-drug set: just-prescribed lines + the patient's active current medications.
        var rxDrugs = prescribed
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => Screen(m.Name, isPrescribed: true, historyId: null))
            .ToList();
        var currentDrugs = context.CurrentMedications
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => Screen(m.Name, isPrescribed: false, historyId: m.HistoryId))
            .ToList();
        var allDrugs = rxDrugs.Concat(currentDrugs).ToList();

        // (1) Allergy cross-reactivity — each just-prescribed drug vs each recorded allergy.
        foreach (var allergy in context.Allergies)
        {
            var allergen = ResolveAllergen(allergy.Substance);
            if (allergen.Canonical is null && allergen.Classes.Count == 0) continue;   // non-drug allergen → skip
            foreach (var rx in rxDrugs)
            {
                var direct = allergen.Canonical is not null && string.Equals(rx.Canonical, allergen.Canonical, StringComparison.Ordinal);
                var crossPenicillin = rx.Classes.Contains("penicillin") && allergen.Classes.Contains("penicillin");
                var crossOther = rx.Classes.Overlaps(new[] { "sulfonamide", "nsaid", "salicylate", "macrolide" }.Where(allergen.Classes.Contains));
                // Across beta-lactam subclasses (penicillin↔cephalosporin, either direction): real but lower-rate
                // cross-reactivity (~1–2%) → moderate caution, when it isn't already a same-class match above.
                var crossBetaLactam = rx.Classes.Contains("betalactam") && allergen.Classes.Contains("betalactam")
                                      && !(direct || crossPenicillin);

                if (direct || crossPenicillin || crossOther)
                    Add(new DrugAlertFinding(DrugAlertType.Allergy, AllergySeverity(allergy.Severity), rx.Display,
                        $"Prescribed {rx.Display} conflicts with a recorded drug allergy ({AllergenLabel(allergen)}). Confirm before dispensing.",
                        allergy.HistoryId));
                else if (crossBetaLactam)
                    Add(new DrugAlertFinding(DrugAlertType.Allergy, DrugAlertSeverity.Moderate, rx.Display,
                        $"Prescribed {rx.Display} (beta-lactam) may cross-react with a recorded beta-lactam allergy. Use caution.",
                        allergy.HistoryId));
            }
        }

        // (2) Interaction rules — over all unordered drug pairs (>=1 just-prescribed).
        foreach (var rule in InteractionRules)
        {
            for (var i = 0; i < allDrugs.Count; i++)
            for (var j = i + 1; j < allDrugs.Count; j++)
            {
                var d1 = allDrugs[i];
                var d2 = allDrugs[j];
                if (!d1.IsPrescribed && !d2.IsPrescribed) continue;                 // only flag what THIS prescription introduces
                if (string.Equals(d1.Canonical, d2.Canonical, StringComparison.Ordinal)) continue;

                var fwd = MatchesAny(d1, rule.A) && MatchesAny(d2, rule.B);
                var rev = MatchesAny(d2, rule.A) && MatchesAny(d1, rule.B);
                if (!fwd && !rev) continue;

                // Name the just-prescribed drug; reference a current med via its linked (encrypted) record, not its name.
                var rx = d1.IsPrescribed ? d1 : d2;
                var other = ReferenceEquals(rx, d1) ? d2 : d1;
                var otherLabel = other.IsPrescribed ? other.Display : "a current medication";
                Add(new DrugAlertFinding(DrugAlertType.Interaction, rule.Severity, rx.Display,
                    $"{rx.Display} + {otherLabel}: {rule.Risk}.",
                    other.IsPrescribed ? null : other.HistoryId));
            }
        }

        // (3) Duplicate therapy — same drug, or same don't-double-up class, introduced by this prescription.
        for (var i = 0; i < rxDrugs.Count; i++)
        {
            var rx = rxDrugs[i];
            // exact-same drug already on the current med list
            foreach (var cur in currentDrugs)
                if (string.Equals(rx.Canonical, cur.Canonical, StringComparison.Ordinal))
                    Add(new DrugAlertFinding(DrugAlertType.Duplicate, DrugAlertSeverity.Moderate, rx.Display,
                        $"Duplicate therapy: {rx.Display} is already on the patient's current medication list.", cur.HistoryId));

            // same drug / same class prescribed twice in this prescription
            for (var j = i + 1; j < rxDrugs.Count; j++)
            {
                var other = rxDrugs[j];
                if (string.Equals(rx.Canonical, other.Canonical, StringComparison.Ordinal))
                    Add(new DrugAlertFinding(DrugAlertType.Duplicate, DrugAlertSeverity.Moderate, rx.Display,
                        $"Duplicate therapy: {rx.Display} is prescribed more than once.", null));
                else
                {
                    // Low-dose cardioprotective aspirin alongside an NSAID is a distinct therapeutic intent, not
                    // duplicate therapy — don't raise a same-class duplicate for that pairing (alert-fatigue guard).
                    var sharedDupClass = rx.Classes.FirstOrDefault(c =>
                        DuplicateClasses.Contains(c) && other.Classes.Contains(c)
                        && !(c == "nsaid" && (rx.Canonical == "aspirin" || other.Canonical == "aspirin")));
                    if (sharedDupClass is not null)
                        Add(new DrugAlertFinding(DrugAlertType.Duplicate, DrugAlertSeverity.Moderate, rx.Display,
                            $"Duplicate therapy: {rx.Display} and {other.Display} are both {sharedDupClass.Replace('_', ' ')} agents.", null));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DrugAlertFinding>>(findings);
    }

    // ---- matching internals ----------------------------------------------------------------------

    private sealed record ScreenedDrug(string Canonical, FrozenSet<string> Classes, string Display, bool IsPrescribed, Guid? HistoryId);

    private static ScreenedDrug Screen(string rawName, bool isPrescribed, Guid? historyId)
    {
        var canonical = ExtractCanonical(rawName);
        var classes = Classes.TryGetValue(canonical, out var cls) ? cls : FrozenSet<string>.Empty;
        var display = Trim200(rawName.Trim());
        return new ScreenedDrug(canonical, classes, display, isPrescribed, historyId);
    }

    /// <summary>Normalize free text and resolve to a canonical generic: scan known drug names/aliases as tokens.</summary>
    private static string ExtractCanonical(string raw)
    {
        var norm = Normalize(raw);
        if (Aliases.TryGetValue(norm, out var direct)) return direct;
        if (Classes.ContainsKey(norm)) return norm;
        // token / substring scan for an embedded known drug ("tab amoxicillin 500mg", "anaphylaxis to amoxicillin")
        foreach (var alias in Aliases)
            if (ContainsToken(norm, alias.Key)) return alias.Value;
        foreach (var known in Classes.Keys)
            if (ContainsToken(norm, known)) return known;
        return norm;   // unknown drug → its normalized name (won't match any class/name selector)
    }

    private (string? Canonical, FrozenSet<string> Classes) ResolveAllergen(string substance)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var canonical = ExtractCanonical(substance);
        if (Classes.TryGetValue(canonical, out var drugClasses))
            foreach (var c in drugClasses) classes.Add(c);
        else
            canonical = null;   // not a specific known drug; rely on keyword classes

        var norm = Normalize(substance);
        foreach (var (keyword, tags) in AllergyKeywords)
            if (ContainsToken(norm, keyword))
                foreach (var t in tags) classes.Add(t);

        return (canonical, classes.ToFrozenSet(StringComparer.Ordinal));
    }

    private static bool MatchesAny(ScreenedDrug drug, string[] selectors) => selectors.Any(sel => Matches(drug, sel));

    private static bool Matches(ScreenedDrug drug, string selector)
    {
        var idx = selector.IndexOf(':');
        var kind = selector[..idx];
        var val = selector[(idx + 1)..];
        return kind switch
        {
            "name" => string.Equals(drug.Canonical, val, StringComparison.Ordinal),
            "class" => drug.Classes.Contains(val),
            _ => false,
        };
    }

    private static DrugAlertSeverity AllergySeverity(string? recordSeverity) => recordSeverity?.ToLowerInvariant() switch
    {
        "critical" or "severe" => DrugAlertSeverity.Critical,
        "moderate" => DrugAlertSeverity.High,
        "mild" => DrugAlertSeverity.Moderate,
        _ => DrugAlertSeverity.High,   // an allergy with no graded severity is treated as high by default
    };

    private static string AllergenLabel((string? Canonical, FrozenSet<string> Classes) a)
    {
        if (a.Classes.Contains("penicillin")) return "penicillin-class";
        if (a.Classes.Contains("sulfonamide")) return "sulfonamide-class";
        if (a.Classes.Contains("nsaid") || a.Classes.Contains("salicylate")) return "NSAID-class";
        if (a.Classes.Contains("macrolide")) return "macrolide-class";
        if (a.Classes.Contains("cephalosporin")) return "cephalosporin-class";
        return "same-agent";
    }

    private static string Normalize(string s)
    {
        var lowered = s.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lowered.Length);
        var prevSpace = false;
        foreach (var ch in lowered)
        {
            if (char.IsWhiteSpace(ch)) { if (!prevSpace && sb.Length > 0) { sb.Append(' '); prevSpace = true; } }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>True if <paramref name="needle"/> appears in <paramref name="haystack"/> on word boundaries
    /// (so "mox" inside "amoxicillin" does NOT match, but "amoxicillin" inside "tab amoxicillin 500" does).</summary>
    private static bool ContainsToken(string haystack, string needle)
    {
        var from = 0;
        while (true)
        {
            var idx = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (idx < 0) return false;
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            var endIdx = idx + needle.Length;
            var afterOk = endIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[endIdx]);
            if (beforeOk && afterOk) return true;
            from = idx + 1;
        }
    }

    private static string Trim200(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>Disabled screening (<c>DrugInteractions:Provider=none</c>): generates no alerts. A fail-SAFE no-op
/// — it never claims "all clear"; screening is simply off (matching the pre-feature behaviour) until a real
/// source is configured.</summary>
public sealed class NullDrugInteractionSource : IDrugInteractionSource
{
    public string SourceName => "disabled";
    public Task<IReadOnlyList<DrugAlertFinding>> EvaluateAsync(
        IReadOnlyList<MedicationInput> prescribed, PatientSafetyContext context, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DrugAlertFinding>>([]);
}
