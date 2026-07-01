using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Commission;
using mediq.SharedDataModel.Docslot.Commission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Commission / broker (Care Partner) surface (slice 07). Compliance: PAN encrypted (never returned),
/// discount↔attribution exclusivity (DB trigger → 422), PCPNDT (DB CHECKs), MCI 6.4 (tenant pays broker —
/// doctors never in the money flow). CRITICAL RBAC: payout APPROVAL and EXECUTION are gated by DISTINCT
/// permission keys (<c>commission.payouts.approve</c> vs <c>commission.payouts.execute</c>).
/// </summary>
[ApiController]
[Route("api/v1/commission")]
[Authorize]
public sealed class CommissionController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    // ---- Brokers ---------------------------------------------------------------------------------

    [HttpGet("brokers")]
    [RequirePermission("commission.broker.read")]
    [ProducesResponseType<IReadOnlyList<BrokerDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BrokerDto>>> ListBrokers([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListBrokersQuery(RequireTenant(), skip, take), ct));

    [HttpPost("brokers")]
    [RequirePermission("commission.broker.invite")]
    [ProducesResponseType<RegisterBrokerResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterBrokerResult>> RegisterBroker([FromBody] RegisterBrokerRequest request, CancellationToken ct)
        => Ok(await commands.Send(new RegisterBrokerCommand(RequireTenant(), request), ct));

    // Suspend and activate are SEPARATE endpoints, each gated by its OWN dangerous permission
    // (commission.broker.suspend vs commission.broker.activate — distinct is_dangerous keys in the seed).
    // They were previously collapsed into one POST /status gated ONLY on .activate, so a caller holding
    // suspend-but-not-activate saw the Suspend button and was rejected 403 (#58). The transition is now
    // implied by the route; both dispatch the same SetBrokerStatusCommand.
    [HttpPost("brokers/{brokerId:guid}/suspend")]
    [RequirePermission("commission.broker.suspend")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SuspendBroker(Guid brokerId, [FromBody] SetBrokerStatusReasonRequest request, CancellationToken ct)
    {
        await commands.Send(new SetBrokerStatusCommand(RequireTenant(), brokerId, new SetBrokerStatusRequest(false, request.Reason)), ct);
        return NoContent();
    }

    [HttpPost("brokers/{brokerId:guid}/activate")]
    [RequirePermission("commission.broker.activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ActivateBroker(Guid brokerId, [FromBody] SetBrokerStatusReasonRequest request, CancellationToken ct)
    {
        await commands.Send(new SetBrokerStatusCommand(RequireTenant(), brokerId, new SetBrokerStatusRequest(true, request.Reason)), ct);
        return NoContent();
    }

    /// <summary>Permanently blacklist a broker — PLATFORM-level dangerous permission.</summary>
    [HttpPost("brokers/{brokerId:guid}/blacklist")]
    [RequirePermission("commission.broker.blacklist")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Blacklist(Guid brokerId, [FromBody] BlacklistBrokerRequest request, CancellationToken ct)
    {
        await commands.Send(new BlacklistBrokerCommand(brokerId, request), ct);
        return NoContent();
    }

    // ---- Self-service (broker views own wallet/links) --------------------------------------------
    // IDOR-safe: the broker's OWN id comes from the server-signed broker_id JWT claim (RequireOwnBroker),
    // NEVER a client-supplied query param — a broker can only ever reach their own wallet/links.

    [HttpGet("me/wallet")]
    [RequirePermission("commission.broker.read_self")]
    [ProducesResponseType<BrokerWalletDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BrokerWalletDto>> MyWallet(CancellationToken ct)
        => Ok(await queries.Query(new GetBrokerWalletQuery(RequireOwnBroker()), ct));

    [HttpPost("me/links")]
    [RequirePermission("commission.broker.generate_link_self")]
    [ProducesResponseType<ReferralLinkDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ReferralLinkDto>> CreateLink([FromBody] CreateReferralLinkRequest request, CancellationToken ct)
        => Ok(await commands.Send(new CreateReferralLinkCommand(RequireOwnBroker(), request), ct));

    [HttpGet("me/links")]
    [RequirePermission("commission.broker.read_self")]
    [ProducesResponseType<IReadOnlyList<ReferralLinkDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReferralLinkDto>>> MyLinks(CancellationToken ct)
        => Ok(await queries.Query(new ListReferralLinksQuery(RequireOwnBroker()), ct));

    /// <summary>
    /// Broker self-service booking on behalf of a referred patient. Creates a behalf booking (patient consent
    /// OTP, DPDP) + an auto-verified <c>broker_portal_booking</c> attribution. IDOR-safe: tenant + broker_id come
    /// from the signed token, never the body. The broker must be active + linked to the tenant (else 403).
    /// </summary>
    [HttpPost("me/bookings")]
    [RequirePermission("commission.broker.create_booking_self")]
    [ProducesResponseType<BrokerBookingResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BrokerBookingResult>> CreateBrokerBooking([FromBody] CreateBrokerBookingRequest request, CancellationToken ct)
        => Ok(await commands.Send(new BrokerPortalBookingCommand(RequireTenant(), RequireOwnBroker(), request), ct));

    // ---- Rules -----------------------------------------------------------------------------------

    [HttpGet("rules")]
    [RequirePermission("commission.rules.read")]
    public async Task<ActionResult<IReadOnlyList<CommissionRuleDto>>> ListRules(CancellationToken ct)
        => Ok(await queries.Query(new ListRulesQuery(RequireTenant()), ct));

    [HttpPost("rules")]
    [RequirePermission("commission.rules.create")]
    public async Task<ActionResult<Guid>> CreateRule([FromBody] CreateCommissionRuleRequest request, CancellationToken ct)
        => Ok(await commands.Send(new CreateRuleCommand(RequireTenant(), request), ct));

    [HttpPost("rules/{ruleId:guid}/approve")]
    [RequirePermission("commission.rules.approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ApproveRule(Guid ruleId, CancellationToken ct)
    {
        await commands.Send(new ApproveRuleCommand(RequireTenant(), ruleId), ct);
        return NoContent();
    }

    // ---- Attribution -----------------------------------------------------------------------------

    // Creating an attribution MINTS commission (money) — gate on the danger 'override' write permission, not
    // the read permission (the prior read gate let any ledger-viewer mint commission).
    [HttpPost("attributions")]
    [RequirePermission("commission.attribution.override")]
    [ProducesResponseType<AttributionResultDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AttributionResultDto>> CreateAttribution([FromBody] CreateAttributionRequest request, CancellationToken ct)
        => Ok(await commands.Send(new CreateAttributionCommand(RequireTenant(), request), ct));

    /// <summary>
    /// File a POST-HOC broker-attribution claim for a booking: mints a 'post_hoc_claim' attribution (pending,
    /// no money earns yet) and sends the patient an OTP to confirm/deny via WhatsApp. On confirm the attribution
    /// earns; on deny / no-response within 24h it reverses. Gated by the dedicated <c>commission.attribution.claim</c>
    /// write permission. The discount↔attribution DB trigger rejects a discounted booking (→ 422).
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/claim-attribution")]
    [RequirePermission("commission.attribution.claim")]
    [ProducesResponseType<ClaimAttributionResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ClaimAttributionResult>> ClaimAttribution(Guid bookingId, [FromBody] ClaimAttributionRequest request, CancellationToken ct)
        => Ok(await commands.Send(new PostHocClaimCommand(RequireTenant(), bookingId, request.BrokerId, request.ClaimedRelation), ct));

    /// <summary>Attribution ledger (most recent first). Patient identity is a first name + masked phone only (DPDP).</summary>
    [HttpGet("attributions")]
    [RequirePermission("commission.attribution.read")]
    [ProducesResponseType<IReadOnlyList<AttributionListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AttributionListItemDto>>> ListAttributions([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListAttributionsQuery(RequireTenant(), skip, take), ct));

    // ---- Payouts: APPROVE ≠ EXECUTE (distinct permission keys) -----------------------------------

    [HttpGet("payouts")]
    [RequirePermission("commission.payouts.read")]
    public async Task<ActionResult<IReadOnlyList<PayoutDto>>> ListPayouts([FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListPayoutsQuery(RequireTenant(), skip, take), ct));

    [HttpPost("payouts/batch")]
    [RequirePermission("commission.payouts.approve")]
    [ProducesResponseType<PayoutDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PayoutDto>> CreateBatch([FromBody] CreatePayoutBatchRequest request, CancellationToken ct)
        => Ok(await commands.Send(new CreatePayoutBatchCommand(RequireTenant(), request), ct));

    /// <summary>STEP 1 — approve a payout for execution. Gated by <c>commission.payouts.approve</c> (tenant scope).</summary>
    [HttpPost("payouts/{payoutId:guid}/approve")]
    [RequirePermission("commission.payouts.approve")]
    [ProducesResponseType<PayoutActionResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PayoutActionResult>> ApprovePayout(Guid payoutId, CancellationToken ct)
        => Ok(await commands.Send(new ApprovePayoutCommand(RequireTenant(), payoutId), ct));

    /// <summary>
    /// STEP 2 — execute the actual transfer. Gated by the DISTINCT <c>commission.payouts.execute</c>
    /// (platform scope). A user who can approve but NOT execute is rejected here (403). Requires prior approval.
    /// </summary>
    [HttpPost("payouts/{payoutId:guid}/execute")]
    [RequirePermission("commission.payouts.execute")]
    [ProducesResponseType<PayoutActionResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PayoutActionResult>> ExecutePayout(Guid payoutId, CancellationToken ct)
        => Ok(await commands.Send(new ExecutePayoutCommand(RequireTenant(), payoutId), ct));

    /// <summary>
    /// Issue a TDS / Form 16A certificate (section 194H) for a PAID payout. The certificate is PROVISIONAL until
    /// the quarterly TDS return is filed on TRACES. Gated by <c>commission.tds.issue</c>.
    /// </summary>
    [HttpPost("payouts/{payoutId:guid}/form-16a")]
    [RequirePermission("commission.tds.issue")]
    [ProducesResponseType<Form16ACertificateDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<Form16ACertificateDto>> IssueForm16A(Guid payoutId, CancellationToken ct)
        => Ok(await commands.Send(new IssueForm16ACommand(RequireTenant(), payoutId), ct));

    /// <summary>
    /// Render the Form 16A document (full deductee PAN — the access is logged to key_usage_log). Dispatched as a
    /// command so that access log commits. 404 until a certificate is issued. Gated by <c>commission.tds.issue</c>.
    /// </summary>
    [HttpGet("payouts/{payoutId:guid}/form-16a/document")]
    [RequirePermission("commission.tds.issue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForm16ADocument(Guid payoutId, CancellationToken ct)
    {
        var doc = await commands.Send(new GetForm16ADocumentCommand(RequireTenant(), payoutId), ct);
        return doc is null ? NotFound() : Content(doc.Content, doc.ContentType);
    }

    // ---- Disputes & campaigns --------------------------------------------------------------------

    [HttpPost("disputes")]
    [RequirePermission("commission.dispute.raise")]
    public async Task<ActionResult<Guid>> RaiseDispute([FromBody] RaiseDisputeRequest request, CancellationToken ct)
        => Ok(await commands.Send(new RaiseDisputeCommand(RequireTenant(), request), ct));

    [HttpPost("disputes/resolve")]
    [RequirePermission("commission.dispute.resolve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResolveDispute([FromBody] ResolveDisputeRequest request, CancellationToken ct)
    {
        await commands.Send(new ResolveDisputeCommand(RequireTenant(), request), ct);
        return NoContent();
    }

    [HttpGet("disputes")]
    [RequirePermission("commission.attribution.read")]
    public async Task<ActionResult<IReadOnlyList<DisputeDto>>> ListDisputes(CancellationToken ct)
        => Ok(await queries.Query(new ListDisputesQuery(RequireTenant()), ct));

    [HttpPost("campaigns")]
    [RequirePermission("commission.campaign.manage")]
    public async Task<ActionResult<Guid>> CreateCampaign([FromBody] CreateCampaignRequest request, CancellationToken ct)
        => Ok(await commands.Send(new CreateCampaignCommand(RequireTenant(), request), ct));

    [HttpGet("campaigns")]
    [RequirePermission("commission.campaign.manage")]
    public async Task<ActionResult<IReadOnlyList<CampaignDto>>> ListCampaigns(CancellationToken ct)
        => Ok(await queries.Query(new ListCampaignsQuery(RequireTenant()), ct));

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

    /// <summary>
    /// The caller's OWN broker id from the server-signed <c>broker_id</c> claim. 403 if the caller is not a
    /// broker (no claim) — a holder of read_self/generate_link_self can only ever act on their own broker.
    /// </summary>
    private Guid RequireOwnBroker() =>
        currentUser.BrokerId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("This endpoint is for broker self-service; no broker identity on this token.");
}
