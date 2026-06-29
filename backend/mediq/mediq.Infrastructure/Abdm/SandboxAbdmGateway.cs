using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Abdm;

/// <summary>
/// Deterministic DEV sandbox for the ABDM network — it simulates the NHA HIP care-context link flow WITHOUT any
/// network call (so the abdm_health_records linkage columns — is_linked_to_phr / linked_at / care_context_id —
/// actually function end-to-end in dev). It validates the ABHA identifier shape (14-digit number or an ABHA
/// address) and returns a deterministic care_context_id derived from the record id. It does NOT mint real ABDM
/// artefacts and is NOT a substitute for the NHA integration — production swaps the real <c>nha</c> adapter
/// behind the same <see cref="IAbdmGateway"/> seam (sandbox/prod client creds + mTLS + the real HIE-CM flows).
/// </summary>
public sealed class SandboxAbdmGateway : IAbdmGateway
{
    public string ProviderName => "sandbox-dev";

    public Task<AbdmLinkResult> LinkCareContextAsync(AbdmLinkRequest request, CancellationToken ct)
    {
        if (!IsValidAbha(request.AbhaNumber))
            return Task.FromResult(new AbdmLinkResult(
                false, null, null, "ABHA number/address is not a valid format (expected a 14-digit ABHA number or an ABHA address)."));

        // Deterministic, idempotent-by-record linkage reference (a real gateway returns a network-assigned id).
        var careContextId = $"CC-{request.RecordId:N}";            // 35 chars ≤ care_context_id VARCHAR(100)
        var gatewayReference = $"SBX-{request.RecordId:N}";
        return Task.FromResult(new AbdmLinkResult(true, careContextId, gatewayReference, null));
    }

    /// <summary>A 14-digit ABHA number (hyphens/spaces tolerated) OR an ABHA address (<c>name@suffix</c>).</summary>
    private static bool IsValidAbha(string? abha)
    {
        if (string.IsNullOrWhiteSpace(abha)) return false;
        var trimmed = abha.Trim();
        if (trimmed.Contains('@'))
        {
            var at = trimmed.IndexOf('@');
            return at > 0 && at < trimmed.Length - 1;              // non-empty local + suffix
        }
        var digits = trimmed.Where(char.IsDigit).Count();
        var nonDigit = trimmed.Count(c => c is not '-' and not ' ' && !char.IsDigit(c));
        return nonDigit == 0 && digits == 14;                     // 14 digits, only digits/hyphens/spaces
    }
}

/// <summary>Disabled ABDM gateway (<c>Abdm:Provider=none</c>): every link is refused with a clear "not
/// configured" reason — an HONEST no-op, never a fake "linked". Used where no ABDM integration is wired (e.g.
/// before NHA credentials exist); the caller maps the decline to a 4xx.</summary>
public sealed class DisabledAbdmGateway : IAbdmGateway
{
    public string ProviderName => "disabled";

    public Task<AbdmLinkResult> LinkCareContextAsync(AbdmLinkRequest request, CancellationToken ct)
        => Task.FromResult(new AbdmLinkResult(
            false, null, null, "ABDM integration is not configured for this environment."));
}
