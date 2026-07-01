using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Security;
using mediq.SharedDataModel.Docslot.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Security/compliance substrate surface (super_admin / DPO gated by the slice-05 seeded permission keys):
/// audit-chain verify/anchor, DPDP portability + cryptographic erasure, breach reporting, break-glass.
/// </summary>
[ApiController]
[Route("api/v1/security")]
[Authorize]
public sealed class SecurityController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    /// <summary>Verify the audit hash chain is intact (returns broken links, if any).</summary>
    [HttpGet("audit-chain/verify")]
    [RequirePermission("platform.audit.verify_chain")]
    [ProducesResponseType<AuditChainVerifyResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditChainVerifyResult>> VerifyAuditChain(CancellationToken ct)
        => Ok(await queries.Query(new VerifyAuditChainQuery(), ct));

    /// <summary>Anchor the current chain head to an external store (transparency log / notary).</summary>
    [HttpPost("audit-chain/anchor")]
    [RequirePermission("platform.audit.anchor")]
    [ProducesResponseType<AuditAnchorResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditAnchorResult>> AnchorAuditChain([FromBody] AnchorRequest body, CancellationToken ct)
        => Ok(await commands.Send(new AnchorAuditChainCommand(body.AnchorType, body.AnchorReference), ct));

    // ---- Audit tab: READ side of platform.audit_log (issue #86) ----------------------------------

    /// <summary>
    /// Paginated, faceted read of THIS tenant's audit trail (tenant bound from the JWT, never a param).
    /// Filters: date range (default last 30 days), category, severity, and a free-text search over
    /// actor/action/target. Returns per-category and per-severity facet counts for the filter rails.
    /// </summary>
    [HttpGet("audit/logs")]
    [RequirePermission("tenant.audit.read")]
    [ProducesResponseType<AuditLogPageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuditLogPageDto>> ListAuditLogs(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? category = null, [FromQuery] string? severity = null,
        [FromQuery] string? search = null, CancellationToken ct = default)
        => Ok(await queries.Query(new ListAuditLogQuery(page, pageSize, from, to, category, severity, search), ct));

    /// <summary>CSV export of the filtered audit trail (same tenant scoping + filters as the list).</summary>
    [HttpGet("audit/logs/export")]
    [RequirePermission("tenant.audit.read")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportAuditLogs(
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? category = null, [FromQuery] string? severity = null,
        [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var csv = await queries.Query(new ExportAuditLogQuery(from, to, category, severity, search), ct);
        return File(System.Text.Encoding.UTF8.GetBytes(csv.Content), "text/csv", csv.FileName);
    }

    // ---- Active-session oversight + admin revoke (issue #87) -------------------------------------

    /// <summary>Active sessions of THIS tenant's members (People "Online" presence). No token material.</summary>
    [HttpGet("sessions")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<IReadOnlyList<ActiveSessionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ActiveSessionDto>>> ListActiveSessions(
        [FromQuery] int take = 200, CancellationToken ct = default)
        => Ok(await queries.Query(new ListActiveSessionsQuery(take), ct));

    /// <summary>Revoke a single session (404 unless the owner is a member of this tenant).</summary>
    [HttpPost("sessions/{sessionId:guid}/revoke")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> RevokeSession(Guid sessionId, CancellationToken ct)
        => Ok(await commands.Send(new RevokeSessionCommand(sessionId), ct));

    /// <summary>Sign out ALL active sessions for a user (403 unless the target is a member of this tenant).</summary>
    [HttpPost("sessions/users/{userId:guid}/revoke-all")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<RevokeAllSessionsResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RevokeAllSessionsResult>> RevokeAllUserSessions(Guid userId, CancellationToken ct)
        => Ok(await commands.Send(new RevokeAllUserSessionsCommand(userId), ct));

    // ---- Read tabs (Security & Compliance console) -----------------------------------------------

    /// <summary>Audit-chain anchor history (most recent first).</summary>
    [HttpGet("audit-chain/anchors")]
    [RequirePermission("platform.audit.read")]
    [ProducesResponseType<IReadOnlyList<AuditAnchorDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditAnchorDto>>> ListAnchors([FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListAuditAnchorsQuery(take), ct));

    /// <summary>DPDP rights requests (export + erasure), unified. Subject identity is a masked phone only.</summary>
    [HttpGet("dpdp/requests")]
    [RequirePermission("platform.export_requests.process")]
    [ProducesResponseType<IReadOnlyList<DpdpRequestDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DpdpRequestDto>>> ListDpdpRequests([FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListDpdpRequestsQuery(take), ct));

    /// <summary>Breach register (72h DPB clock fields).</summary>
    [HttpGet("breaches")]
    [RequirePermission("platform.breach.read")]
    [ProducesResponseType<IReadOnlyList<BreachDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BreachDto>>> ListBreaches([FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListBreachesQuery(take), ct));

    /// <summary>Security review queue (break-glass + anomalies awaiting review). Masked subject/actor only.</summary>
    [HttpGet("review-queue")]
    [RequirePermission("platform.anomalies.review")]
    [ProducesResponseType<IReadOnlyList<ReviewQueueItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReviewQueueItemDto>>> ListReviewQueue([FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListReviewQueueQuery(take), ct));

    /// <summary>
    /// Impersonation-session oversight (issue #3): active + recently-ended support sessions, newest first.
    /// Metadata only — masked actor, target tenant label, derived status. Gated by the review permission.
    /// </summary>
    [HttpGet("impersonation-sessions")]
    [RequirePermission("platform.anomalies.review")]
    [ProducesResponseType<IReadOnlyList<ImpersonationSessionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ImpersonationSessionDto>>> ListImpersonationSessions(
        [FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListImpersonationSessionsQuery(take), ct));

    /// <summary>Encryption-key rotation status (metadata only — NO key material).</summary>
    [HttpGet("keys")]
    [RequirePermission("platform.encryption_keys.read")]
    [ProducesResponseType<IReadOnlyList<KeyStatusDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<KeyStatusDto>>> ListKeys(CancellationToken ct)
        => Ok(await queries.Query(new ListKeyStatusQuery(), ct));

    /// <summary>Deletion certificates (post-erasure compliance lookup). Subject is a masked phone only.</summary>
    [HttpGet("deletion-certificates")]
    [RequirePermission("platform.deletion.certify")]
    [ProducesResponseType<IReadOnlyList<DeletionCertificateDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeletionCertificateDto>>> ListDeletionCertificates([FromQuery] int take = 100, CancellationToken ct = default)
        => Ok(await queries.Query(new ListDeletionCertificatesQuery(take), ct));

    /// <summary>DPDP §11 portability: assemble a subject's data into a FHIR-R4 export bundle.</summary>
    [HttpPost("dpdp/export")]
    [RequirePermission("platform.export_requests.process")]
    [ProducesResponseType<DataExportResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DataExportResult>> Export([FromBody] ExportRequest body, CancellationToken ct)
        => Ok(await commands.Send(new ExportSubjectDataCommand(body.SubjectPhone), ct));

    /// <summary>DPDP §12 erasure: cryptographically erase a subject (destroy keys → ciphertext unrecoverable).</summary>
    [HttpPost("dpdp/erase")]
    [RequirePermission("platform.deletion.certify")]
    [ProducesResponseType<ErasureResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ErasureResult>> Erase([FromBody] EraseRequest body, CancellationToken ct)
        => Ok(await commands.Send(new EraseSubjectDataCommand(body.DeletionRequestId, body.SubjectPhone), ct));

    /// <summary>DPDP §8(6) breach reporting (creates a breach_log row with the 72h DPB clock).</summary>
    [HttpPost("breaches")]
    [RequirePermission("platform.breach.read")]
    [ProducesResponseType<Guid>(StatusCodes.Status201Created)]
    public async Task<ActionResult<Guid>> ReportBreach([FromBody] BreachRequest body, CancellationToken ct)
        => Ok(await commands.Send(new ReportBreachCommand(body.BreachType, body.Severity, body.Description), ct));

    /// <summary>
    /// Break-glass emergency access — issues a scoped, time-boxed grant over a patient's clinical records so a
    /// consent-denied read can proceed (FR-MED-03). Mandatory justification; logged + flagged for review.
    /// <c>ResourceId</c> null = patient-wide for the resource type. ABDM is not grantable (separate regime).
    /// </summary>
    [HttpPost("break-glass")]
    [RequirePermission("docslot.medical_access.break_glass")]
    [ProducesResponseType<Guid>(StatusCodes.Status200OK)]
    public async Task<ActionResult<Guid>> BreakGlass([FromBody] BreakGlassRequest body, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId
            ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
        return Ok(await commands.Send(new BreakGlassCommand(tenantId, body.PatientId, body.ResourceType, body.ResourceId, body.Justification), ct));
    }

    /// <summary>Revoke an active break-glass grant early (reviewer action — DISTINCT permission from granting).</summary>
    [HttpPost("break-glass/{grantId:guid}/revoke")]
    [RequirePermission("platform.anomalies.review")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> RevokeBreakGlass(Guid grantId, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId
            ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
        return Ok(await commands.Send(new RevokeBreakGlassCommand(tenantId, grantId), ct));
    }

    public sealed record AnchorRequest(string AnchorType, string AnchorReference);
    public sealed record ExportRequest(string SubjectPhone);
    public sealed record EraseRequest(Guid DeletionRequestId, string SubjectPhone);
    public sealed record BreachRequest(string BreachType, string Severity, string Description);
    public sealed record BreakGlassRequest(Guid PatientId, string ResourceType, Guid? ResourceId, string Justification);
}
