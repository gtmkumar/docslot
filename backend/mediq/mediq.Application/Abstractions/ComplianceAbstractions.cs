namespace mediq.Application.Abstractions;

/// <summary>DPDP §11 portability: assembles a patient's data into an export bundle (FHIR R4 structure).</summary>
public interface IDataExportService
{
    Task<DataExportResult> AssembleAsync(Guid requestId, string subjectPhone, Guid byUserId, CancellationToken ct);
    Task<Guid> CreateRequestAsync(string subjectPhone, string format, Guid? requesterUserId, CancellationToken ct);
}

public sealed record DataExportResult(Guid RequestId, string Format, string BundleJson, int RecordCount, string Checksum);

/// <summary>
/// DPDP §12 right-to-erasure via CRYPTOGRAPHIC erasure: destroy the subject's encryption key(s), record a
/// <c>deletion_certificate</c> (keys destroyed + before/after hashes + signature). NEVER physically deletes
/// rows — ciphertext stays (FKs intact) but is permanently unrecoverable.
/// </summary>
public interface ICryptoErasureService
{
    Task<ErasureResult> EraseAsync(Guid deletionRequestId, string subjectPhone, Guid certifiedByUserId, CancellationToken ct);
}

public sealed record ErasureResult(Guid CertificateId, IReadOnlyList<Guid> DestroyedKeyIds, string PreHash, string PostHash);

/// <summary>DPDP §8(6) breach reporting into <c>platform.breach_log</c> (72h DPB timeline field).</summary>
public interface IBreachReportingService
{
    Task<Guid> CreateAsync(string breachType, string severity, string description, Guid byUserId, CancellationToken ct);
    Task MarkReportedToDpbAsync(Guid breachId, Guid byUserId, CancellationToken ct);
}

/// <summary>DPDP §6 immutable consent event log into <c>platform.consent_event_log</c> (append-only).</summary>
public interface IConsentEventLogger
{
    Task RecordAsync(ConsentEvent evt, CancellationToken ct);
}

public sealed record ConsentEvent(
    string PatientPhone, Guid? TenantId, string EventType, string ConsentScopeJson,
    string? LegalBasis, string? Channel, Guid? ActorUserId, string? IpAddress);

/// <summary>Audit-chain verification (DPDP §8(7) accountability) + external anchoring of the chain head.</summary>
public interface IAuditChainService
{
    /// <summary>Runs <c>platform.verify_audit_chain()</c>; returns the broken links (empty = intact).</summary>
    Task<IReadOnlyList<AuditChainBreak>> VerifyAsync(CancellationToken ct);

    /// <summary>Records the current chain head into <c>platform.audit_anchors</c> for external tamper-proofing.</summary>
    Task<AuditAnchorResult> AnchorAsync(string anchorType, string anchorReference, Guid byUserId, CancellationToken ct);
}

public sealed record AuditChainBreak(long Sequence, Guid AuditId, string ExpectedHash, string ActualHash);
public sealed record AuditAnchorResult(Guid AnchorId, long HeadSequence, string HeadHash);

/// <summary>
/// Break-glass emergency access (Layer 2): grants access AFTER a mandatory justification, logs to
/// <c>platform.purpose_of_use_log</c> with <c>is_break_glass=true</c> + <c>review_required=true</c> (surfaces
/// in <c>v_security_review_queue</c>), and marks the patient for notification (WhatsApp send in slice 09).
/// </summary>
public interface IBreakGlassService
{
    Task<Guid> GrantAsync(Guid userId, Guid tenantId, string resourceType, Guid resourceId, string justification, CancellationToken ct);
}

/// <summary>
/// Read side of the Security &amp; Compliance console (slice 05). Projects the anchor history, DPDP rights
/// requests, breach register, review queue, key-rotation status, and deletion certificates for the
/// console's read tabs. SENSITIVE: subject identity is masked at this seam (the infra impl applies the
/// phone mask); key rows carry NO key material. Returns the SharedDataModel read DTOs directly.
/// </summary>
public interface ISecurityReadService
{
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.AuditAnchorDto>> ListAnchorsAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.DpdpRequestDto>> ListDpdpRequestsAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.BreachDto>> ListBreachesAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.ReviewQueueItemDto>> ListReviewQueueAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.KeyStatusDto>> ListKeyStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.DeletionCertificateDto>> ListDeletionCertificatesAsync(int take, CancellationToken ct);
    Task<IReadOnlyList<SharedDataModel.Docslot.Security.ImpersonationSessionDto>> ListImpersonationSessionsAsync(int take, CancellationToken ct);
    Task<DateTimeOffset?> GetLastAnchorAtAsync(CancellationToken ct);
}
