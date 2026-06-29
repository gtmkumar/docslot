namespace mediq.Application.Options;

/// <summary>Selects the <c>IAbdmGateway</c> adapter. Dev defaults to a deterministic sandbox; prod sets the real
/// NHA adapter out-of-band (with sandbox/prod client credentials + mTLS). <c>none</c> disables ABDM linking
/// (link requests are refused with a clear "not configured" — never a fake success).</summary>
public sealed class AbdmGatewayOptions
{
    public const string SectionName = "Abdm";

    /// <summary>'sandbox' (dev default) | 'none' (disabled). Prod sets 'nha'.</summary>
    public string Provider { get; set; } = "sandbox";
}
