namespace mediq.Application.Abstractions;

/// <summary>
/// The seam to the ABDM (Ayushman Bharat Digital Mission) network — India's NHA health-data exchange. In
/// production this is the real NHA HIP/HIU adapter (M2 ABHA verification, HIE-CM consent artefacts, FHIR data
/// flows); in dev it is a deterministic sandbox. Behind this interface so the rest of the app is
/// network-agnostic: the caller persists records locally and asks the gateway to publish/link them.
///
/// SCOPE (this slice wires care-context LINKING — the HIP data-push side): after a FHIR record is stored
/// locally, <see cref="LinkCareContextAsync"/> notifies the ABDM network that a care context exists for the
/// patient's ABHA, returning the network <c>care_context_id</c>. The consent-request (HIE-CM) and HIU record-
/// fetch flows are the next operations to add behind this same seam. The real <c>nha</c> adapter is deferred to
/// credentialed go-live (NHA sandbox client id/secret + mTLS); the dev <c>sandbox</c> adapter simulates the flow
/// deterministically. A network call MUST run OUTSIDE any DB transaction (the handler does this via the
/// self-managed-transaction pattern) so no row lock is held across the I/O.
/// </summary>
public interface IAbdmGateway
{
    /// <summary>Provenance recorded on linked records / audit (e.g. <c>sandbox-dev</c>, <c>nha</c>, <c>disabled</c>).</summary>
    string ProviderName { get; }

    /// <summary>
    /// Publishes a stored health record as a care context to the ABDM network for the patient's ABHA (HIP data
    /// flow). Returns <see cref="AbdmLinkResult.Linked"/>=true with the network <c>care_context_id</c> on success,
    /// or Linked=false with a <see cref="AbdmLinkResult.FailureReason"/> (invalid ABHA, gateway not configured,
    /// network decline) — never throws on an ordinary decline (the caller maps a decline to a 4xx).
    /// </summary>
    Task<AbdmLinkResult> LinkCareContextAsync(AbdmLinkRequest request, CancellationToken ct);
}

/// <summary>A request to link a stored record's care context to the ABDM network.</summary>
public sealed record AbdmLinkRequest(
    Guid TenantId, Guid PatientId, Guid RecordId, string AbhaNumber, string RecordType, string? ConsentId);

/// <summary>The gateway's verdict. <paramref name="CareContextId"/> is the network linkage reference to persist
/// (NOT PHI); <paramref name="GatewayReference"/> is a transaction id for traceability.</summary>
public sealed record AbdmLinkResult(
    bool Linked, string? CareContextId, string? GatewayReference, string? FailureReason);
