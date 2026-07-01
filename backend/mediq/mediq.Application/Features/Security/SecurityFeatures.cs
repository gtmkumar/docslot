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

// ---- Audit tab: read platform.audit_log (issue #86) ----------------------------------------------

/// <summary>
/// Reads the WRITE-only <c>platform.audit_log</c> for the Audit tab. STRICTLY tenant-scoped: the tenant is
/// bound from the server-signed context (<see cref="ICurrentUserContext.TenantId"/>), never a client param,
/// so a caller can only ever see their own tenant's trail. The window defaults to the last 30 days.
/// Gated by <c>tenant.audit.read</c>.
/// </summary>
public sealed record ListAuditLogQuery(
    int Page = 1, int PageSize = 50, DateTimeOffset? From = null, DateTimeOffset? To = null,
    string? Category = null, string? Severity = null, string? Search = null) : IQuery<AuditLogPageDto>;

public sealed class ListAuditLogQueryHandler(ISecurityReadService reads, ICurrentUserContext ctx)
    : IQueryHandler<ListAuditLogQuery, AuditLogPageDto>
{
    public Task<AuditLogPageDto> Handle(ListAuditLogQuery q, CancellationToken ct)
        => reads.ReadAuditLogAsync(ctx.TenantId, ResolveFilter(q), ct);

    internal static AuditLogFilter ResolveFilter(ListAuditLogQuery q)
    {
        var to = q.To ?? DateTimeOffset.UtcNow;
        var from = q.From ?? to.AddDays(-30);
        return new AuditLogFilter(
            Math.Max(1, q.Page), Math.Clamp(q.PageSize, 1, 200), from, to, q.Category, q.Severity, q.Search);
    }
}

/// <summary>CSV export of the filtered audit trail (same tenant scoping + filters as the list). Capped at 10k rows.</summary>
public sealed record ExportAuditLogQuery(
    DateTimeOffset? From = null, DateTimeOffset? To = null,
    string? Category = null, string? Severity = null, string? Search = null) : IQuery<AuditCsvResult>;

public sealed class ExportAuditLogQueryHandler(ISecurityReadService reads, ICurrentUserContext ctx)
    : IQueryHandler<ExportAuditLogQuery, AuditCsvResult>
{
    private const int Cap = 10_000;

    public async Task<AuditCsvResult> Handle(ExportAuditLogQuery q, CancellationToken ct)
    {
        var filter = ListAuditLogQueryHandler.ResolveFilter(
            new ListAuditLogQuery(1, 1, q.From, q.To, q.Category, q.Severity, q.Search));
        var rows = await reads.ReadAuditLogRowsForExportAsync(ctx.TenantId, filter, Cap, ct);
        var fileName = $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return new AuditCsvResult(fileName, AuditCsv.Build(rows));
    }
}

/// <summary>Assembles the audit CSV. Deliberately omits hash-chain internals and any PHI beyond resource_label.</summary>
public static class AuditCsv
{
    private static readonly string[] Header =
        ["occurred_at", "category", "severity", "action", "actor_name", "actor_email",
         "impersonator", "resource_type", "resource_label", "resource_id", "ip_address", "success", "error_code"];

    public static string Build(IReadOnlyList<AuditLogRowDto> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(',', Header));
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', new[]
            {
                r.OccurredAt.UtcDateTime.ToString("o"),
                Csv(r.Category), Csv(r.Severity), Csv(r.Action), Csv(r.ActorName), Csv(r.ActorEmail),
                Csv(r.ImpersonatorName), Csv(r.ResourceType), Csv(r.ResourceLabel), Csv(r.ResourceId?.ToString()),
                Csv(r.IpAddress), r.Success ? "true" : "false", Csv(r.ErrorCode),
            }));
        return sb.ToString();
    }

    /// <summary>RFC-4180 field quoting; also neutralises leading =,+,-,@ to defuse CSV/formula injection.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@')) v = "'" + v;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r'))
            v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
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

/// <summary>
/// Issues a scoped, time-boxed emergency-access grant over a patient's clinical records (FR-MED-03).
/// <c>ResourceType</c> is one clinical record class; <c>ResourceId</c> null = patient-wide for that class.
/// ABDM is intentionally NOT grantable here (the grant table CHECK excludes it).
/// </summary>
public sealed record BreakGlassCommand(Guid TenantId, Guid PatientId, string ResourceType, Guid? ResourceId, string Justification) : ICommand<Guid>;

/// <summary>The clinical record classes a break-glass grant may cover (ABDM excluded — separate NHA regime).</summary>
public static class BreakGlassResourceTypes
{
    public static readonly string[] Allowed = ["prescription", "lab_report", "medical_history"];
}

public sealed class BreakGlassValidator : AbstractValidator<BreakGlassCommand>
{
    public BreakGlassValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ResourceType).NotEmpty()
            .Must(rt => BreakGlassResourceTypes.Allowed.Contains(rt))
            .WithMessage("Break-glass covers prescription, lab_report, or medical_history only (ABDM is not overridable).");
        // ResourceId is OPTIONAL: null = patient-wide for the resource type.
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
        var grantId = await breakGlass.GrantAsync(
            userId, command.TenantId, command.PatientId, command.ResourceType, command.ResourceId, command.Justification, ct);
        await audit.RecordAsync(new AuditEntry(
            "break_glass", command.ResourceType, command.ResourceId ?? command.PatientId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Break-glass emergency access granted (scoped, time-boxed, flagged for review)", Purpose: "emergency"), ct);
        return grantId;
    }
}

// ---- Break-glass revoke (reviewer ends a grant early) --------------------------------------------

/// <summary>Revokes an active break-glass grant (reviewer action; gated by the distinct review permission).</summary>
public sealed record RevokeBreakGlassCommand(Guid TenantId, Guid GrantId) : ICommand<bool>;

public sealed class RevokeBreakGlassValidator : AbstractValidator<RevokeBreakGlassCommand>
{
    public RevokeBreakGlassValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

public sealed class RevokeBreakGlassCommandHandler(
    IBreakGlassService breakGlass, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeBreakGlassCommand, bool>
{
    public async Task<bool> Handle(RevokeBreakGlassCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var revoked = await breakGlass.RevokeAsync(command.GrantId, command.TenantId, userId, ct);
        if (revoked)
            await audit.RecordAsync(new AuditEntry(
                "revoke", "break_glass_grant", command.GrantId, null, ctx.UserId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: "Break-glass grant revoked by reviewer", Purpose: "audit"), ct);
        return revoked;
    }
}

// ---- Active-session oversight + admin revoke (issue #87) ------------------------------------------

/// <summary>
/// Lists ACTIVE sessions of the caller-tenant's members (People "Online" presence). Tenant is bound from the
/// server-signed context; the current user id sets the per-row self flag. Gated by <c>tenant.users.update</c>.
/// </summary>
public sealed record ListActiveSessionsQuery(int Take = 200) : IQuery<IReadOnlyList<ActiveSessionDto>>;

public sealed class ListActiveSessionsQueryHandler(ISessionAdminService sessions, ICurrentUserContext ctx)
    : IQueryHandler<ListActiveSessionsQuery, IReadOnlyList<ActiveSessionDto>>
{
    public async Task<IReadOnlyList<ActiveSessionDto>> Handle(ListActiveSessionsQuery q, CancellationToken ct)
    {
        // No tenant on the token (e.g. a platform actor without an active tenant) → nothing to present here.
        if (ctx.TenantId is not { } tenantId) return [];
        return await sessions.ListActiveForTenantAsync(tenantId, ctx.UserId, Math.Clamp(q.Take, 1, 500), ct);
    }
}

/// <summary>Revokes a single session — refused (404) unless its owner is a member of the caller's tenant.</summary>
public sealed record RevokeSessionCommand(Guid SessionId) : ICommand<bool>;

public sealed class RevokeSessionValidator : AbstractValidator<RevokeSessionCommand>
{
    public RevokeSessionValidator() => RuleFor(x => x.SessionId).NotEmpty();
}

public sealed class RevokeSessionCommandHandler(
    ISessionAdminService sessions, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeSessionCommand, bool>
{
    public async Task<bool> Handle(RevokeSessionCommand command, CancellationToken ct)
    {
        var tenantId = ctx.TenantId
            ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

        var revoked = await sessions.RevokeMemberSessionAsync(command.SessionId, tenantId, "revoked_by_admin", ct);
        if (!revoked)
            // Absent, already-revoked, or owned by a non-member → refuse without leaking existence.
            throw new KeyNotFoundException("Session not found for a member of this tenant.");

        await audit.RecordAsync(new AuditEntry(
            "revoke", "user_session", command.SessionId, null, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Admin revoked a user session", Purpose: "security"), ct);
        return true;
    }
}

/// <summary>Signs out ALL active sessions for one user — refused (403) unless the target is a tenant member.</summary>
public sealed record RevokeAllUserSessionsCommand(Guid TargetUserId) : ICommand<RevokeAllSessionsResult>;

public sealed class RevokeAllUserSessionsValidator : AbstractValidator<RevokeAllUserSessionsCommand>
{
    public RevokeAllUserSessionsValidator() => RuleFor(x => x.TargetUserId).NotEmpty();
}

public sealed class RevokeAllUserSessionsCommandHandler(
    ISessionAdminService sessions, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeAllUserSessionsCommand, RevokeAllSessionsResult>
{
    public async Task<RevokeAllSessionsResult> Handle(RevokeAllUserSessionsCommand command, CancellationToken ct)
    {
        var tenantId = ctx.TenantId
            ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

        // Throws ForbiddenException (→ 403) if the target is not a member of this tenant (cross-tenant guard).
        var count = await sessions.RevokeAllForMemberAsync(command.TargetUserId, tenantId, "revoked_all_by_admin", ct);

        await audit.RecordAsync(new AuditEntry(
            "revoke", "user_session", command.TargetUserId, null, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Admin signed out all sessions for user {command.TargetUserId} ({count})", Purpose: "security"), ct);
        return new RevokeAllSessionsResult(command.TargetUserId, count);
    }
}
