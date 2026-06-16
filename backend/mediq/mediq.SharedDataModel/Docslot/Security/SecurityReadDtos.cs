namespace mediq.SharedDataModel.Docslot.Security;

// Read DTOs for the Security & Compliance console (slice 05). SENSITIVE SURFACE:
//  - Subject identity is a MASKED phone only — raw subject_phone is never serialised.
//  - Key rows carry metadata only — NO key material.
//  - Mirrors the FE contracts: AuditAnchorSchema, DpdpRequestSchema, BreachSchema,
//    ReviewQueueItemSchema, KeyStatusSchema (+ a deletion-certificate metadata view).

/// <summary>One past audit-chain anchor (maps to <c>platform.audit_anchors</c>). Mirrors FE AuditAnchorSchema.</summary>
public sealed record AuditAnchorDto(
    Guid AnchorId,
    long ChainHeadSequence,
    string ChainHeadHash,
    string AnchorType,
    string AnchorReference,
    DateTimeOffset AnchoredAt);

/// <summary>
/// A DPDP rights request (export / erasure / correction), unified across
/// <c>data_export_requests</c> + <c>data_deletion_requests</c>. Mirrors FE DpdpRequestSchema.
/// PHI: subject is a MASKED phone only.
/// </summary>
public sealed record DpdpRequestDto(
    Guid RequestId,
    string Kind,                         // export | erasure | correction
    string SubjectMaskedPhone,
    string Status,                       // pending | processing | completed | rejected
    string Scope,
    string? Reason,
    DateTimeOffset? GracePeriodEndsAt,
    DateTimeOffset CreatedAt);

/// <summary>A breach register row (maps to <c>platform.breach_log</c>). Mirrors FE BreachSchema (72h DPB clock).</summary>
public sealed record BreachDto(
    Guid BreachId,
    string BreachType,
    string Severity,                     // low | medium | high | critical
    string Description,
    int? AffectedRecordCount,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ReportedToDpbAt,
    DateTimeOffset? ResolvedAt);

/// <summary>
/// A security review-queue item (maps to <c>platform.v_security_review_queue</c>): break-glass grants +
/// anomalies awaiting review. Mirrors FE ReviewQueueItemSchema. PHI: only a masked subject ref / actor label.
/// </summary>
public sealed record ReviewQueueItemDto(
    string Source,                       // anomaly | break_glass | consent_revocation
    Guid ItemId,
    string Severity,
    DateTimeOffset OccurredAt,
    string Description,
    string? ActorLabel,
    string? SubjectMaskedPhone);

/// <summary>An encryption-key health row (maps to <c>platform.v_key_rotation_status</c>). NO key material.</summary>
public sealed record KeyStatusDto(
    Guid KeyId,
    string? TenantName,
    string DataClass,
    DateTimeOffset ActivatedAt,
    DateTimeOffset? NextRotationDueAt,
    string RotationStatus,               // ok | due_soon | overdue
    int? DaysUntilRotation,
    long UsageCount);

/// <summary>
/// A deletion certificate metadata row (maps to <c>platform.deletion_certificates</c>). Mirrors the
/// fields the FE certificate view shows. PHI: subject is a MASKED phone; the digital signature/hashes are
/// integrity proofs (not PHI). Returned for audit/compliance lookups after the once-shown erase result.
/// </summary>
public sealed record DeletionCertificateDto(
    Guid CertificateId,
    Guid DeletionRequestId,
    string SubjectMaskedPhone,
    IReadOnlyList<Guid> DestroyedKeyIds,
    string PreDeletionHash,
    string PostDeletionHash,
    string SignatureAlgorithm,
    string DigitalSignature,
    DateTimeOffset CertifiedAt,
    IReadOnlyDictionary<string, int> DeletedRecordCounts);
