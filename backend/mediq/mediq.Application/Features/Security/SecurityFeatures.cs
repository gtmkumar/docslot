using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Security;

namespace mediq.Application.Features.Security;

// ---- Audit chain verify + anchor -----------------------------------------------------------------

/// <summary>Verifies the audit hash chain (DPDP §8(7)). Gated by <c>platform.audit.verify_chain</c>.</summary>
public sealed record VerifyAuditChainQuery : IQuery<AuditChainVerifyResult>;

/// <summary><c>lastVerifiedAt</c> is surfaced as the most recent audit-anchor time (the FE's "last known-good"
/// reference) — the raw verify call has no timestamp of its own, so we reuse the last external anchor.</summary>
public sealed record AuditChainVerifyResult(bool Intact, IReadOnlyList<AuditChainBreak> Breaks, DateTimeOffset? LastVerifiedAt);

public sealed class VerifyAuditChainQueryHandler(IAuditChainService chain, ISecurityReadService reads)
    : IQueryHandler<VerifyAuditChainQuery, AuditChainVerifyResult>
{
    public async Task<AuditChainVerifyResult> Handle(VerifyAuditChainQuery q, CancellationToken ct)
    {
        var breaks = await chain.VerifyAsync(ct);
        var lastAnchorAt = await reads.GetLastAnchorAtAsync(ct);
        return new AuditChainVerifyResult(breaks.Count == 0, breaks, lastAnchorAt);
    }
}

// ---- Security console read lists (gated read tabs) ----------------------------------------------

/// <summary>Anchor history (most recent first). Gated by <c>platform.audit.read</c>.</summary>
public sealed record ListAuditAnchorsQuery(int Take = 100) : IQuery<IReadOnlyList<AuditAnchorDto>>;

public sealed class ListAuditAnchorsQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListAuditAnchorsQuery, IReadOnlyList<AuditAnchorDto>>
{
    public Task<IReadOnlyList<AuditAnchorDto>> Handle(ListAuditAnchorsQuery q, CancellationToken ct)
        => reads.ListAnchorsAsync(Math.Clamp(q.Take, 1, 500), ct);
}

/// <summary>Unified DPDP rights requests (export + erasure). Gated by <c>platform.export_requests.process</c>.</summary>
public sealed record ListDpdpRequestsQuery(int Take = 100) : IQuery<IReadOnlyList<DpdpRequestDto>>;

public sealed class ListDpdpRequestsQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListDpdpRequestsQuery, IReadOnlyList<DpdpRequestDto>>
{
    public Task<IReadOnlyList<DpdpRequestDto>> Handle(ListDpdpRequestsQuery q, CancellationToken ct)
        => reads.ListDpdpRequestsAsync(Math.Clamp(q.Take, 1, 500), ct);
}

/// <summary>Breach register (72h DPB clock fields). Gated by <c>platform.breach.read</c>.</summary>
public sealed record ListBreachesQuery(int Take = 100) : IQuery<IReadOnlyList<BreachDto>>;

public sealed class ListBreachesQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListBreachesQuery, IReadOnlyList<BreachDto>>
{
    public Task<IReadOnlyList<BreachDto>> Handle(ListBreachesQuery q, CancellationToken ct)
        => reads.ListBreachesAsync(Math.Clamp(q.Take, 1, 500), ct);
}

/// <summary>Review queue (break-glass + anomalies awaiting review). Gated by <c>platform.anomalies.review</c>.</summary>
public sealed record ListReviewQueueQuery(int Take = 100) : IQuery<IReadOnlyList<ReviewQueueItemDto>>;

public sealed class ListReviewQueueQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListReviewQueueQuery, IReadOnlyList<ReviewQueueItemDto>>
{
    public Task<IReadOnlyList<ReviewQueueItemDto>> Handle(ListReviewQueueQuery q, CancellationToken ct)
        => reads.ListReviewQueueAsync(Math.Clamp(q.Take, 1, 500), ct);
}

/// <summary>Encryption-key rotation status (metadata only). Gated by <c>platform.encryption_keys.read</c>.</summary>
public sealed record ListKeyStatusQuery : IQuery<IReadOnlyList<KeyStatusDto>>;

public sealed class ListKeyStatusQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListKeyStatusQuery, IReadOnlyList<KeyStatusDto>>
{
    public Task<IReadOnlyList<KeyStatusDto>> Handle(ListKeyStatusQuery q, CancellationToken ct)
        => reads.ListKeyStatusAsync(ct);
}

/// <summary>Deletion certificates (post-erasure compliance lookup). Gated by <c>platform.deletion.certify</c>.</summary>
public sealed record ListDeletionCertificatesQuery(int Take = 100) : IQuery<IReadOnlyList<DeletionCertificateDto>>;

public sealed class ListDeletionCertificatesQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListDeletionCertificatesQuery, IReadOnlyList<DeletionCertificateDto>>
{
    public Task<IReadOnlyList<DeletionCertificateDto>> Handle(ListDeletionCertificatesQuery q, CancellationToken ct)
        => reads.ListDeletionCertificatesAsync(Math.Clamp(q.Take, 1, 500), ct);
}

/// <summary>Impersonation-session oversight list (issue #3). Gated by <c>platform.anomalies.review</c>.</summary>
public sealed record ListImpersonationSessionsQuery(int Take = 100) : IQuery<IReadOnlyList<ImpersonationSessionDto>>;

public sealed class ListImpersonationSessionsQueryHandler(ISecurityReadService reads)
    : IQueryHandler<ListImpersonationSessionsQuery, IReadOnlyList<ImpersonationSessionDto>>
{
    public Task<IReadOnlyList<ImpersonationSessionDto>> Handle(ListImpersonationSessionsQuery q, CancellationToken ct)
        => reads.ListImpersonationSessionsAsync(Math.Clamp(q.Take, 1, 500), ct);
}

public sealed record AnchorAuditChainCommand(string AnchorType, string AnchorReference) : ICommand<AuditAnchorResult>;

public sealed class AnchorAuditChainValidator : AbstractValidator<AnchorAuditChainCommand>
{
    public AnchorAuditChainValidator()
    {
        RuleFor(x => x.AnchorType).NotEmpty();
        RuleFor(x => x.AnchorReference).NotEmpty();
    }
}

public sealed class AnchorAuditChainCommandHandler(
    IAuditChainService chain, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<AnchorAuditChainCommand, AuditAnchorResult>
{
    public async Task<AuditAnchorResult> Handle(AnchorAuditChainCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var result = await chain.AnchorAsync(command.AnchorType, command.AnchorReference, userId, ct);
        await audit.RecordAsync(new AuditEntry(
            "anchor", "audit_anchor", result.AnchorId, $"seq {result.HeadSequence}", ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Audit chain head anchored"), ct);
        return result;
    }
}

// ---- DPDP portability (export) -------------------------------------------------------------------

public sealed record ExportSubjectDataCommand(string SubjectPhone) : ICommand<DataExportResult>;

public sealed class ExportSubjectDataValidator : AbstractValidator<ExportSubjectDataCommand>
{
    public ExportSubjectDataValidator() => RuleFor(x => x.SubjectPhone).NotEmpty();
}

public sealed class ExportSubjectDataCommandHandler(
    IDataExportService export, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<ExportSubjectDataCommand, DataExportResult>
{
    public async Task<DataExportResult> Handle(ExportSubjectDataCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var requestId = await export.CreateRequestAsync(command.SubjectPhone, "fhir_r4_bundle", userId, ct);
        var result = await export.AssembleAsync(requestId, command.SubjectPhone, userId, ct);
        await audit.RecordAsync(new AuditEntry(
            "export", "data_export_request", requestId, command.SubjectPhone, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Assembled portability export ({result.RecordCount} records)", Purpose: "patient_request", LegalBasis: "consent"), ct);
        return result;
    }
}

// ---- DPDP erasure (cryptographic) ----------------------------------------------------------------

public sealed record EraseSubjectDataCommand(Guid DeletionRequestId, string SubjectPhone) : ICommand<ErasureResult>;

public sealed class EraseSubjectDataValidator : AbstractValidator<EraseSubjectDataCommand>
{
    public EraseSubjectDataValidator()
    {
        RuleFor(x => x.DeletionRequestId).NotEmpty();
        RuleFor(x => x.SubjectPhone).NotEmpty();
    }
}

public sealed class EraseSubjectDataCommandHandler(
    ICryptoErasureService erasure, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<EraseSubjectDataCommand, ErasureResult>
{
    public async Task<ErasureResult> Handle(EraseSubjectDataCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var result = await erasure.EraseAsync(command.DeletionRequestId, command.SubjectPhone, userId, ct);
        await audit.RecordAsync(new AuditEntry(
            "erase", "deletion_certificate", result.CertificateId, command.SubjectPhone, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Cryptographic erasure: {result.DestroyedKeyIds.Count} keys destroyed", LegalBasis: "legal_obligation"), ct);
        return result;
    }
}

// ---- Breach reporting ----------------------------------------------------------------------------

public sealed record ReportBreachCommand(string BreachType, string Severity, string Description) : ICommand<Guid>;

public sealed class ReportBreachValidator : AbstractValidator<ReportBreachCommand>
{
    public ReportBreachValidator()
    {
        RuleFor(x => x.BreachType).NotEmpty();
        RuleFor(x => x.Severity).Must(s => s is "low" or "medium" or "high" or "critical");
        RuleFor(x => x.Description).NotEmpty();
    }
}

public sealed class ReportBreachCommandHandler(
    IBreachReportingService breach, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<ReportBreachCommand, Guid>
{
    public async Task<Guid> Handle(ReportBreachCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var id = await breach.CreateAsync(command.BreachType, command.Severity, command.Description, userId, ct);
        await audit.RecordAsync(new AuditEntry(
            "report_breach", "breach_log", id, command.BreachType, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: $"Breach reported ({command.Severity})"), ct);
        return id;
    }
}

// ---- Break-glass emergency access ----------------------------------------------------------------

public sealed record BreakGlassCommand(Guid TenantId, string ResourceType, Guid ResourceId, string Justification) : ICommand<Guid>;

public sealed class BreakGlassValidator : AbstractValidator<BreakGlassCommand>
{
    public BreakGlassValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ResourceType).NotEmpty();
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.Justification).NotEmpty().MinimumLength(10)
            .WithMessage("Break-glass requires a substantive justification.");
    }
}

public sealed class BreakGlassCommandHandler(
    IBreakGlassService breakGlass, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<BreakGlassCommand, Guid>
{
    public async Task<Guid> Handle(BreakGlassCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var logId = await breakGlass.GrantAsync(userId, command.TenantId, command.ResourceType, command.ResourceId, command.Justification, ct);
        await audit.RecordAsync(new AuditEntry(
            "break_glass", command.ResourceType, command.ResourceId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Break-glass emergency access granted (flagged for review)", Purpose: "emergency"), ct);
        return logId;
    }
}
