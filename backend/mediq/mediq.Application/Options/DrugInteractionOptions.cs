namespace mediq.Application.Options;

/// <summary>Selects the <c>IDrugInteractionSource</c> adapter. Dev defaults to a curated, well-known ruleset;
/// prod swaps a licensed interaction database (First Databank / Medi-Span / RxNorm+DDInter) out-of-band.
/// <c>none</c> disables screening (no alerts generated — a fail-SAFE no-op, never a false "all clear").</summary>
public sealed class DrugInteractionOptions
{
    public const string SectionName = "DrugInteractions";

    /// <summary>'curated_dev' (dev default) | 'none' (disabled). Prod adds 'fdb' / 'medispan' / 'rxnorm'.</summary>
    public string Provider { get; set; } = "curated_dev";
}
