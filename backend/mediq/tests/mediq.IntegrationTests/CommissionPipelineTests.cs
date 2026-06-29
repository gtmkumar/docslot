using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Commission;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 commission MONEY PIPELINE end-to-end against the live canonical DB. Exercises the formerly-dead
/// flow now wired: attribution (pending, wallet pending_inr) → booking COMPLETED earns it (→earned, wallet
/// pending→earned) → settlement window elapses (→ready_to_pay, wallet earned→ready_to_pay) → payout batch
/// (gross→TDS/GST→net, ₹100 GROSS floor) → approve → execute via DRY-RUN gateway (→paid, wallet
/// ready_to_pay→lifetime_paid). Cancel/no-show OR a tenant-wins dispute REVERSES (→reversed, wallet debited +
/// lifetime_reversed). COMPLIANCE-flagged: payout math, attribution/payout RLS, attribution.override authz,
/// MCI/PCPNDT-adjacent money flow → requires security-compliance-auditor sign-off.
/// <para>
/// Each test SEEDS ITS OWN completable booking + slot (in the fixture tenant, with the fixture's single
/// broker/wallet) and cleans them up, so the cases are order-independent. Wallet deltas are asserted relative
/// to the value read immediately before the action under test, never an absolute (the broker wallet is shared).
/// </para>
/// </summary>
[Collection("CommissionPipeline")]
public sealed class CommissionPipelineTests(CommissionPipelineWebAppFactory factory)
{
    private const decimal Commission = CommissionPipelineWebAppFactory.FlatCommission;   // ₹200

    // ---- 1. EARNING: attribution credits pending; booking COMPLETE moves pending→earned -------------

    [Fact]
    public async Task Earning_AttributionCreditsPending_ThenBookingCompleteMovesPendingToEarned()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            var (pending0, earned0) = await PendingEarnedAsync();

            var attr = await CreateAttributionAsync(super, bookingId);
            Assert.Equal("auto_verified", attr.VerificationStatus);
            Assert.Equal("pending", attr.CommissionStatus);
            Assert.Equal(Commission, attr.CommissionAmountInr);

            // Wallet: pending_inr increased by the commission; earned unchanged.
            var (pending1, earned1) = await PendingEarnedAsync();
            Assert.Equal(pending0 + Commission, pending1);
            Assert.Equal(earned0, earned1);

            await ApproveBookingAsync(super, bookingId);
            await CompleteBookingAsync(super, bookingId);

            // Attribution earned (exercises MarkEarnedForBookingAsync — EF SqlQueryRaw over UPDATE...RETURNING).
            Assert.Equal("earned", await AttrStatusAsync(bookingId));
            Assert.True(await AttrEarnedAtSetAsync(bookingId), "earned_at must be set after completion");

            // Wallet: pending decreased back, earned increased by the commission.
            var (pending2, earned2) = await PendingEarnedAsync();
            Assert.Equal(pending0, pending2);
            Assert.Equal(earned0 + Commission, earned2);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 2. SETTLEMENT: earned → ready_to_pay; wallet earned→ready_to_pay --------------------------

    [Fact]
    public async Task Settlement_EarnedAttributionMovesToReadyToPay_AndWalletEarnedToReadyToPay()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);

            var earned0 = (await PendingEarnedAsync()).Earned;
            var ready0 = await WalletAsync("ready_to_pay_inr");

            var settled = await SettleAsync();
            Assert.True(settled >= 1, "at least one earned attribution should settle to ready_to_pay");
            Assert.Equal("ready_to_pay", await AttrStatusAsync(bookingId));

            // Wallet: earned bucket drained into ready_to_pay by the commission.
            var earned1 = (await PendingEarnedAsync()).Earned;
            var ready1 = await WalletAsync("ready_to_pay_inr");
            Assert.Equal(earned0 - Commission, earned1);
            Assert.Equal(ready0 + Commission, ready1);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 3. PAYOUT DRY-RUN END-TO-END: batch (non-empty) → approve → execute → paid ----------------

    [Fact]
    public async Task Payout_DryRunEndToEnd_BatchApproveExecute_MarksPaid_AndMovesWalletToLifetimePaid()
    {
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);
            await SettleAsync();

            var ready0 = await WalletAsync("ready_to_pay_inr");
            var paid0 = await WalletAsync("lifetime_paid_inr");
            Assert.True(ready0 >= 100m, $"ready_to_pay must be ≥ ₹100 to form a batch (was {ready0})");

            // Batch (finance has commission.payouts.approve). Must now be NON-EMPTY (gross = ready_to_pay sum).
            var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
                new CreatePayoutBatchRequest(factory.BrokerId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
            Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
            var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
            Assert.True(payout!.AttributionCount >= 1, "the batch must aggregate at least one ready_to_pay attribution");
            Assert.Equal(ready0, payout.GrossAmountInr);
            Assert.True(payout.GrossAmountInr >= 100m);

            // Approve (finance) → execute (super; only role with commission.payouts.execute).
            var approve = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/approve", new { });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            var execResp = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.OK, execResp.StatusCode);
            var result = await execResp.Content.ReadFromJsonAsync<PayoutActionResult>();
            Assert.Equal("paid", result!.Status);

            // HONEST dry run: a DRYRUN- reference + the stub gateway name — NOT a fabricated UTR.
            Assert.StartsWith("DRYRUN-", result.PaymentReference);
            var (reference, gateway) = await PayoutRefAndGatewayAsync(payout.PayoutId);
            Assert.StartsWith("DRYRUN-", reference!);
            Assert.Equal("stub_dryrun", gateway);

            // Batch attributions → paid.
            Assert.Equal("paid", await AttrStatusAsync(bookingId));

            // Wallet: ready_to_pay drained by gross; lifetime_paid increased by gross.
            var ready1 = await WalletAsync("ready_to_pay_inr");
            var paid1 = await WalletAsync("lifetime_paid_inr");
            Assert.Equal(ready0 - payout.GrossAmountInr, ready1);
            Assert.Equal(paid0 + payout.GrossAmountInr, paid1);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 3b. DOUBLE-EXECUTE GUARD: execute is atomic-claim; a repeat execute is rejected, no double-credit -

    [Fact]
    public async Task Payout_DoubleExecute_CreditsWalletOnce_AndSecondExecuteIsIdempotent()
    {
        // Regression for auditor P2 Finding 1 (HIGH) + the gateway-go-live F2 / ExecutePayout-idempotency fix:
        // execute is two-phase (durable approved→'processing' claim, gateway OUTSIDE the tx, single-winner
        // finalize), so a repeat execute cannot double-disburse or double-credit the wallet. A second execute
        // on the already-'paid' batch is an IDEMPOTENT replay → 200 'paid' with the SAME recorded reference, and
        // the wallet/attributions are untouched (NOT a 422 — a retried money op that already succeeded must
        // report success, not an error).
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);
            await SettleAsync();

            var ready0 = await WalletAsync("ready_to_pay_inr");
            var paid0 = await WalletAsync("lifetime_paid_inr");

            var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
                new CreatePayoutBatchRequest(factory.BrokerId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
            Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
            var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
            var approve = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

            // FIRST execute: succeeds, credits the wallet exactly once.
            var exec1 = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.OK, exec1.StatusCode);
            Assert.Equal("paid", (await exec1.Content.ReadFromJsonAsync<PayoutActionResult>())!.Status);

            var paidAfter1 = await WalletAsync("lifetime_paid_inr");
            var readyAfter1 = await WalletAsync("ready_to_pay_inr");
            Assert.Equal(paid0 + payout.GrossAmountInr, paidAfter1);     // credited once, by the gross
            Assert.Equal(ready0 - payout.GrossAmountInr, readyAfter1);   // ready_to_pay drained
            Assert.Equal(0m, readyAfter1);                               // ...to zero
            Assert.Equal("paid", await AttrStatusAsync(bookingId));

            var ref1 = (await PayoutRefAndGatewayAsync(payout.PayoutId)).Reference;

            // SECOND execute on the SAME 'paid' payout: IDEMPOTENT replay → 200 'paid' with the same reference.
            var exec2 = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.OK, exec2.StatusCode);
            var replay = await exec2.Content.ReadFromJsonAsync<PayoutActionResult>();
            Assert.Equal("paid", replay!.Status);
            Assert.Equal(ref1, replay.PaymentReference);   // the recorded reference, not a freshly-minted one

            // No double-credit: lifetime_paid + ready_to_pay are UNCHANGED by the replayed second call, the
            // attributions are still 'paid' (not re-processed), and the payout stays 'paid'.
            Assert.Equal(paidAfter1, await WalletAsync("lifetime_paid_inr"));
            Assert.Equal(readyAfter1, await WalletAsync("ready_to_pay_inr"));
            Assert.Equal("paid", await AttrStatusAsync(bookingId));
            Assert.Equal("paid", await PayoutStatusAsync(payout.PayoutId));
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task Payout_ResumeFromProcessing_FinalizesExactlyOnce()
    {
        // Crash-recovery (gateway-go-live F2 / idempotency HIGH): simulates a crash AFTER Phase 1 durably claimed
        // the payout (approved → 'processing') but BEFORE Phase 3 finalized. The next execute must RESUME — re-call
        // the gateway (idempotent) and finalize, crediting the wallet EXACTLY ONCE (not skipped, not doubled).
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);
            await SettleAsync();
            var ready0 = await WalletAsync("ready_to_pay_inr");
            var paid0 = await WalletAsync("lifetime_paid_inr");

            var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
                new CreatePayoutBatchRequest(factory.BrokerId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
            var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
            await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });

            // The durable claim that survived a crash: approved → 'processing', finalize never ran.
            await ExecAsync("UPDATE commission.payouts SET status='processing' WHERE payout_id=@p", ("p", payout.PayoutId));

            // RESUME via a normal execute: finalizes (does not reject the non-'approved' state, does not double).
            var resume = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            Assert.Equal("paid", (await resume.Content.ReadFromJsonAsync<PayoutActionResult>())!.Status);

            Assert.Equal("paid", await PayoutStatusAsync(payout.PayoutId));
            Assert.Equal("paid", await AttrStatusAsync(bookingId));
            Assert.Equal(paid0 + payout.GrossAmountInr, await WalletAsync("lifetime_paid_inr"));   // credited once
            Assert.Equal(ready0 - payout.GrossAmountInr, await WalletAsync("ready_to_pay_inr"));    // drained once
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task Payout_GatewayThrows_LeavesProcessing_NotFailed_AndResumeFinalizesOnce()
    {
        // The gateway raising (network/timeout) is AMBIGUOUS — the transfer may have happened. The handler must
        // leave the payout 'processing' (so a later execute resumes via the idempotency key) and must NOT mark it
        // 'failed' (which would release it for a fresh disbursement → double-pay) nor credit the wallet. A later
        // execute through a healthy gateway then finalizes exactly once.
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(await ClientAsync(factory.SuperEmail), bookingId);
            await SettleAsync();
            var ready0 = await WalletAsync("ready_to_pay_inr");
            var paid0 = await WalletAsync("lifetime_paid_inr");

            var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
                new CreatePayoutBatchRequest(factory.BrokerId,
                    DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
            var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
            await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });

            // A derived app whose payout gateway THROWS, over the SAME seeded DB.
            using var throwingApp = factory.WithWebHostBuilder(b =>
                b.ConfigureTestServices(s => s.AddScoped<IPayoutGateway, ThrowingPayoutGateway>()));
            var superThrows = await BearerClientAsync(throwingApp, factory.SuperEmail);

            // Execute → gateway throws → 500, but the claim is durable: status stays 'processing', no money moves.
            var exec = await superThrows.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.InternalServerError, exec.StatusCode);
            Assert.Equal("processing", await PayoutStatusAsync(payout.PayoutId));   // NOT 'failed'
            Assert.Equal(ready0, await WalletAsync("ready_to_pay_inr"));            // ready_to_pay preserved
            Assert.Equal(paid0, await WalletAsync("lifetime_paid_inr"));            // not credited
            Assert.Equal("ready_to_pay", await AttrStatusAsync(bookingId));        // attribution not marked paid

            // RESUME through the healthy (stub) gateway → finalizes exactly once.
            var super = await ClientAsync(factory.SuperEmail);
            var resume = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            Assert.Equal("paid", await PayoutStatusAsync(payout.PayoutId));
            Assert.Equal(paid0 + payout.GrossAmountInr, await WalletAsync("lifetime_paid_inr"));   // credited once
            Assert.Equal(ready0 - payout.GrossAmountInr, await WalletAsync("ready_to_pay_inr"));
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    /// <summary>A payout gateway that always throws — models an ambiguous network/timeout failure mid-disbursement.</summary>
    private sealed class ThrowingPayoutGateway : IPayoutGateway
    {
        public string Name => "throwing_test";
        public Task<PayoutGatewayResult> SendAsync(PayoutInstruction instruction, CancellationToken ct) =>
            throw new InvalidOperationException("simulated gateway timeout");
    }

    // ---- 4. REVERSAL ON CANCEL/NO-SHOW: a pending attribution is reversed; wallet pending debited ---

    [Fact]
    public async Task Reversal_OnBookingCancel_AttributionReversed_AndWalletPendingDebited_LifetimeReversedUp()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            // Attribution credits PENDING; cancel (a pre-completion terminal) reverses it.
            await CreateAttributionAsync(super, bookingId);
            Assert.Equal("pending", await AttrStatusAsync(bookingId));

            var pending0 = (await PendingEarnedAsync()).Pending;
            var rev0 = await WalletAsync("lifetime_reversed_inr");

            await CancelBookingAsync(super, bookingId, "patient withdrew");

            Assert.Equal("reversed", await AttrStatusAsync(bookingId));
            var pending1 = (await PendingEarnedAsync()).Pending;
            var rev1 = await WalletAsync("lifetime_reversed_inr");
            Assert.Equal(pending0 - Commission, pending1);
            Assert.Equal(rev0 + Commission, rev1);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task Reversal_OnBookingNoShow_PendingAttributionReversed_AndWalletPendingDebited()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            // No-show on a confirmed (not completed) booking reverses its still-pending attribution: the booking
            // never earned, so the PENDING wallet bucket is debited (earned-bucket reversal is covered by test 5,
            // the tenant-wins dispute, which reverses an EARNED attribution on a completed booking).
            await CreateAttributionAsync(super, bookingId);
            await ApproveBookingAsync(super, bookingId);           // pending → confirmed (attribution still pending)
            Assert.Equal("pending", await AttrStatusAsync(bookingId));

            var pending0 = (await PendingEarnedAsync()).Pending;
            var rev0 = await WalletAsync("lifetime_reversed_inr");

            await NoShowBookingAsync(super, bookingId);

            Assert.Equal("reversed", await AttrStatusAsync(bookingId));
            var pending1 = (await PendingEarnedAsync()).Pending;
            var rev1 = await WalletAsync("lifetime_reversed_inr");
            Assert.Equal(pending0 - Commission, pending1);
            Assert.Equal(rev0 + Commission, rev1);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 5. DISPUTE CLAWBACK: tenant-wins reverses an EARNED attribution; broker-wins does NOT ------

    [Fact]
    public async Task Dispute_TenantWins_ReversesEarnedAttribution_AndDebitsEarned_BrokerWins_DoesNot()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);                              // earned attribution on a completed booking
            var attributionId = await AttrIdAsync(bookingId);
            Assert.NotEqual(Guid.Empty, attributionId);
            Assert.Equal("earned", await AttrStatusAsync(bookingId));

            // broker-wins resolution does NOT reverse.
            var earnedBW0 = (await PendingEarnedAsync()).Earned;
            var revBW0 = await WalletAsync("lifetime_reversed_inr");
            var brokerWins = await RaiseDisputeAsync(super, attributionId, "tenant_staff", "patient_dispute");
            var resolveBW = await super.PostAsJsonAsync("/api/v1/commission/disputes/resolve",
                new ResolveDisputeRequest(brokerWins, "resolved_broker_wins", "broker keeps it", null));
            Assert.Equal(HttpStatusCode.NoContent, resolveBW.StatusCode);
            Assert.Equal("earned", await AttrStatusAsync(bookingId));        // unchanged
            Assert.Equal(earnedBW0, (await PendingEarnedAsync()).Earned);
            Assert.Equal(revBW0, await WalletAsync("lifetime_reversed_inr"));

            // tenant-wins resolution REVERSES + debits the 'earned' bucket + bumps lifetime_reversed.
            var earnedTW0 = (await PendingEarnedAsync()).Earned;
            var revTW0 = await WalletAsync("lifetime_reversed_inr");
            var tenantWins = await RaiseDisputeAsync(super, attributionId, "tenant_staff", "incorrect_attribution");
            var resolveTW = await super.PostAsJsonAsync("/api/v1/commission/disputes/resolve",
                new ResolveDisputeRequest(tenantWins, "resolved_tenant_wins", "clawback", null));
            Assert.Equal(HttpStatusCode.NoContent, resolveTW.StatusCode);

            Assert.Equal("reversed", await AttrStatusAsync(bookingId));
            Assert.Equal(earnedTW0 - Commission, (await PendingEarnedAsync()).Earned);
            Assert.Equal(revTW0 + Commission, await WalletAsync("lifetime_reversed_inr"));
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 6. TIERED RULE: commission = the matching band amount --------------------------------------

    [Fact]
    public async Task TieredRule_AttributionCommissionEqualsBandAmount()
    {
        var super = await ClientAsync(factory.SuperEmail);

        // Tiered rule at HIGHER priority than the flat ₹200 rule so the engine picks it. Bands:
        //   [min:0,   max:500,  amount:100]   (lower band)
        //   [min:500, max:null, amount:250]   (open-top band — exercises the null/absent max path)
        // A ₹800 booking value lands in the open-top band → commission ₹250 (NOT the flat ₹200).
        const decimal expectedBand = 250.00m;
        var ruleId = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO commission.commission_rules
                (rule_id, tenant_id, rule_name, rule_key, calc_type, tiered_table, priority, excludes_pndt, is_active, effective_from, created_at, updated_at)
            VALUES (@id, @t, 'Tiered P2', @key, 'tiered_table', @tt::jsonb, 500, true, true, NOW(), NOW(), NOW())
            """,
            ("id", ruleId), ("t", factory.TenantId), ("key", $"tiered_{ruleId:N}"[..30]),
            ("tt", """[{"min":0,"max":500,"amount":100},{"min":500,"max":null,"amount":250}]"""));

        // A dedicated ₹800-fee doctor so the booking value falls in the open-top band [500, ∞).
        var tierDoctorId = Guid.NewGuid();
        await ExecAsync("INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr Tier',800.00,true,true,NOW(),NOW())",
            ("id", tierDoctorId), ("t", factory.TenantId));
        var (bookingId, slotId) = await SeedCompletableBookingAsync(tierDoctorId);
        try
        {
            var attr = await CreateAttributionAsync(super, bookingId);
            // Exercises CommissionCalculator tiered_table parse: ₹800 → open-top band amount ₹250.
            Assert.Equal(expectedBand, attr.CommissionAmountInr);
        }
        finally
        {
            await CleanBookingAsync(bookingId, slotId);
            await ExecAsync("DELETE FROM commission.commission_rules WHERE rule_id=@r", ("r", ruleId));
            await ExecAsync("DELETE FROM docslot.doctors WHERE doctor_id=@d", ("d", tierDoctorId));
        }
    }

    // ---- 7. AUTHZ: creating an attribution is denied (403) without commission.attribution.override --

    [Fact]
    public async Task Authz_CreateAttribution_DeniedForReadOnlyUser_WithoutOverridePermission()
    {
        // readonly user is tenant_staff: has commission.attribution.read but NOT .override.
        var readonlyClient = await ClientAsync(factory.ReadonlyEmail);
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            var resp = await readonlyClient.PostAsJsonAsync("/api/v1/commission/attributions",
                new CreateAttributionRequest(bookingId, factory.BrokerId, "referral_link", null, null));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.Null(await AttrStatusAsync(bookingId));   // nothing minted
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 8. CAMPAIGN BONUS: on top of base commission, budget-capped, refunded on reversal -----------

    [Fact]
    public async Task CampaignBonus_FlatPerBooking_IsAddedOnTopOfBaseCommission_AndSpendsBudget()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("flat_bonus_per_booking", 100m);
        try
        {
            var pending0 = (await PendingEarnedAsync()).Pending;

            var attr = await CreateAttributionAsync(super, bookingId);
            Assert.Equal(Commission + 100m, attr.CommissionAmountInr);                 // ₹200 base + ₹100 bonus
            Assert.Equal(Commission + 100m, await AttrCommissionAsync(bookingId));
            Assert.Equal(100m, await AttrBonusAsync(bookingId));                        // split recorded in source_metadata
            Assert.Equal(campaignId, await AttrCampaignIdAsync(bookingId));
            Assert.Equal(100m, await CampaignSpentAsync(campaignId));                   // budget reserved
            Assert.Equal(pending0 + Commission + 100m, (await PendingEarnedAsync()).Pending);   // wallet credits the total
        }
        finally { await CleanCampaignAsync(campaignId); await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task CampaignBonus_PercentageMultiplier_AddsThePercentageOfBase()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("percentage_multiplier", 1.5m);       // 1.5× → +50% of base
        try
        {
            var attr = await CreateAttributionAsync(super, bookingId);
            Assert.Equal(Commission * 1.5m, attr.CommissionAmountInr);                 // ₹200 + ₹100 = ₹300
            Assert.Equal(Commission * 0.5m, await AttrBonusAsync(bookingId));          // bonus = ₹100
            Assert.Equal(Commission * 0.5m, await CampaignSpentAsync(campaignId));
        }
        finally { await CleanCampaignAsync(campaignId); await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task CampaignBonus_NeverExceedsBudget_AndStopsOnceExhausted()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (booking1, slot1) = await SeedCompletableBookingAsync();
        var (booking2, slot2) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("flat_bonus_per_booking", 100m, budget: 50m);   // ₹100 bonus, ₹50 budget
        try
        {
            // First booking: the ₹100 bonus is CAPPED to the ₹50 remaining budget.
            var attr1 = await CreateAttributionAsync(super, booking1);
            Assert.Equal(Commission + 50m, attr1.CommissionAmountInr);                 // base + capped bonus
            Assert.Equal(50m, await CampaignSpentAsync(campaignId));                   // budget fully spent

            // Second booking: budget exhausted → NO bonus, base only.
            var attr2 = await CreateAttributionAsync(super, booking2);
            Assert.Equal(Commission, attr2.CommissionAmountInr);
            Assert.Equal(0m, await AttrBonusAsync(booking2));
            Assert.Equal(50m, await CampaignSpentAsync(campaignId));                   // unchanged (never overspent)
        }
        finally
        {
            await CleanCampaignAsync(campaignId);
            await CleanBookingAsync(booking1, slot1);
            await CleanBookingAsync(booking2, slot2);
        }
    }

    [Fact]
    public async Task CampaignBonus_OnReversal_IsRefundedToTheBudget_AndWalletDebitedByTheTotal()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("flat_bonus_per_booking", 100m, budget: 500m);
        try
        {
            var pending0 = (await PendingEarnedAsync()).Pending;
            await CreateAttributionAsync(super, bookingId);
            Assert.Equal(100m, await CampaignSpentAsync(campaignId));                  // reserved
            Assert.Equal(pending0 + Commission + 100m, (await PendingEarnedAsync()).Pending);

            // Cancel → the attribution reverses → the DB trigger refunds the bonus to the campaign budget.
            await CancelBookingAsync(super, bookingId, "patient withdrew");
            Assert.Equal("reversed", await AttrStatusAsync(bookingId));
            Assert.Equal(0m, await CampaignSpentAsync(campaignId));                    // budget refunded
            Assert.Equal(pending0, (await PendingEarnedAsync()).Pending);              // wallet debited by base+bonus
        }
        finally { await CleanCampaignAsync(campaignId); await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task CampaignBonus_MinBookingsThreshold_UnlocksOnlyFromTheNthBooking()
    {
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (booking1, slot1) = await SeedCompletableBookingAsync();
        var (booking2, slot2) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("flat_bonus_per_booking", 100m, minBookings: 2);
        try
        {
            // 1st qualifying booking: below the 2-booking threshold → base only, no budget spent.
            var attr1 = await CreateAttributionAsync(super, booking1);
            Assert.Equal(Commission, attr1.CommissionAmountInr);
            Assert.Equal(0m, await CampaignSpentAsync(campaignId));

            // 2nd booking: threshold met → bonus applies.
            var attr2 = await CreateAttributionAsync(super, booking2);
            Assert.Equal(Commission + 100m, attr2.CommissionAmountInr);
            Assert.Equal(100m, await CampaignSpentAsync(campaignId));
        }
        finally
        {
            await CleanCampaignAsync(campaignId);
            await CleanBookingAsync(booking1, slot1);
            await CleanBookingAsync(booking2, slot2);
        }
    }

    [Fact]
    public async Task CampaignBonus_Refund_IsIdempotent_AcrossTerminalStatusShuffle()
    {
        // The refund must fire only on the FIRST entry into a terminal status. A reversed→rejected shuffle must
        // NOT refund the bonus twice (which would push spent_so_far below the truly-committed spend / leak budget).
        var super = await ClientAsync(factory.SuperEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        var campaignId = await SeedCampaignAsync("flat_bonus_per_booking", 100m, budget: 500m);
        try
        {
            await CreateAttributionAsync(super, bookingId);
            Assert.Equal(100m, await CampaignSpentAsync(campaignId));

            await ExecAsync("UPDATE commission.attributions SET commission_status='reversed' WHERE booking_id=@b AND commission_status<>'reversed'", ("b", bookingId));
            Assert.Equal(0m, await CampaignSpentAsync(campaignId));                    // refunded once

            await ExecAsync("UPDATE commission.attributions SET commission_status='rejected' WHERE booking_id=@b", ("b", bookingId));
            Assert.Equal(0m, await CampaignSpentAsync(campaignId));                    // NOT refunded again
        }
        finally { await CleanCampaignAsync(campaignId); await CleanBookingAsync(bookingId, slotId); }
    }

    // ---- 9. INVOICE NUMBERING + FORM 16A (TDS u/s 194H) ----------------------------------------------

    [Fact]
    public async Task Payout_GetsAutoInvoiceNumber_AndForm16ADocumentIs404BeforeIssue()
    {
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            var payout = await DriveToPaidPayoutAsync(super, finance, bookingId);
            Assert.Matches(@"^INV-\d{6}-\d{5}$", await PayoutInvoiceNumberAsync(payout.PayoutId));   // auto-generated
            // The document 404s until a certificate is issued.
            var doc = await finance.GetAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a/document");
            Assert.Equal(HttpStatusCode.NotFound, doc.StatusCode);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task Form16A_IssuedForPaidPayout_IsProvisional_WithFyQuarterPanLast4_TanOnDocument()
    {
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        await SetTenantTanAsync(factory.TenantId, "BLRT00123A");
        await SetBrokerPanAsync(factory.BrokerId, "ABCPK1234M");
        try
        {
            var payout = await DriveToPaidPayoutAsync(super, finance, bookingId);

            var resp = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a", new { });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var cert = await resp.Content.ReadFromJsonAsync<Form16ACertificateDto>();
            Assert.Equal("194H", cert!.Section);
            Assert.Equal("provisional", cert.Status);                 // never fabricates a TRACES number
            Assert.Null(cert.TracesCertificateNumber);
            Assert.False(string.IsNullOrWhiteSpace(cert.FinancialYear));
            Assert.Matches("^Q[1-4]$", cert.Quarter);
            Assert.Equal(payout.GrossAmountInr, cert.GrossAmountInr);
            Assert.Equal(payout.TdsAmountInr, cert.TdsAmountInr);
            Assert.Equal("BLRT00123A", cert.DeductorTan);
            Assert.Equal("234M", cert.DeducteePanLast4);              // last 4 of ABCPK1234M only
            Assert.Contains($"/payouts/{payout.PayoutId}/form-16a/document", cert.DocumentUrl);
            Assert.Equal(cert.DocumentUrl, await PayoutForm16AUrlAsync(payout.PayoutId));

            // Render the legal document — full PAN appears here (transiently decrypted), NOT in the stored row.
            var doc = await finance.GetAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a/document");
            Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
            Assert.StartsWith("text/html", doc.Content.Headers.ContentType!.ToString());
            var html = await doc.Content.ReadAsStringAsync();
            Assert.Contains("FORM NO. 16A", html);
            Assert.Contains("PROVISIONAL", html);
            Assert.Contains("ABCPK1234M", html);                     // full PAN on the certificate
            Assert.Contains("BLRT00123A", html);                     // deductor TAN
            // The stored cert row holds only the last 4, never the full PAN.
            Assert.Equal("234M", await ScalarAsync<string>("SELECT deductee_pan_last4 FROM commission.tds_certificates WHERE payout_id=@p", ("p", payout.PayoutId)));
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM commission.tds_certificates WHERE payout_id=@p AND deductee_pan_last4='ABCPK1234M'", ("p", payout.PayoutId)));
        }
        finally
        {
            await ExecAsync("UPDATE commission.brokers SET pan_number=NULL WHERE broker_id=@b", ("b", factory.BrokerId));
            await SetTenantTanAsync(factory.TenantId, null);
            await CleanBookingAsync(bookingId, slotId);
        }
    }

    [Fact]
    public async Task Form16A_Document_WithIdempotencyKeyHeader_NeverPersistsFullPanToIdempotencyStore()
    {
        // Regression (auditor HIGH): the document command must NOT have its full-PAN response cached in the
        // durable (plaintext, non-crypto-erasable) idempotency store, even when the request carries an
        // Idempotency-Key header. The IDoNotCacheResponse marker bypasses the cache for this command.
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        await SetBrokerPanAsync(factory.BrokerId, "ABCPK1234M");
        try
        {
            var payout = await DriveToPaidPayoutAsync(super, finance, bookingId);
            await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a", new { });

            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/commission/payouts/{payout.PayoutId}/form-16a/document");
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var doc = await finance.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
            Assert.Contains("ABCPK1234M", await doc.Content.ReadAsStringAsync());   // full PAN served in the document

            // ...but never written at rest to the idempotency store.
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys WHERE response_payload LIKE '%ABCPK1234M%'"));
        }
        finally
        {
            await ExecAsync("UPDATE commission.brokers SET pan_number=NULL WHERE broker_id=@b", ("b", factory.BrokerId));
            await CleanBookingAsync(bookingId, slotId);
        }
    }

    [Fact]
    public async Task Form16A_RejectedForNonPaidPayout_422()
    {
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            await EarnAsync(super, bookingId);
            await SettleAsync();
            var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
                new CreatePayoutBatchRequest(factory.BrokerId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
            var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
            await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });

            // Approved but NOT executed → not 'paid' → Form 16A must be refused.
            var resp = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a", new { });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task Form16A_Issue_DeniedForUserWithoutTdsIssuePermission()
    {
        var super = await ClientAsync(factory.SuperEmail);
        var finance = await ClientAsync(factory.FinanceEmail);
        var readonlyClient = await ClientAsync(factory.ReadonlyEmail);   // tenant_staff — no commission.tds.issue
        await ResetWalletAsync();
        var (bookingId, slotId) = await SeedCompletableBookingAsync();
        try
        {
            var payout = await DriveToPaidPayoutAsync(super, finance, bookingId);
            var resp = await readonlyClient.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/form-16a", new { });
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await CleanBookingAsync(bookingId, slotId); }
    }

    // ================================ helpers ======================================================

    private Task<HttpClient> ClientAsync(string email) => BearerClientAsync(factory, email);

    /// <summary>Logs in against the given app (the base fixture, or a WithWebHostBuilder-derived variant) and returns a bearer client.</summary>
    private async Task<HttpClient> BearerClientAsync(WebApplicationFactory<Program> app, string email)
    {
        var client = app.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, CommissionPipelineWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private async Task<AttributionResultDto> CreateAttributionAsync(HttpClient client, Guid bookingId)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/commission/attributions",
            new CreateAttributionRequest(bookingId, factory.BrokerId, "referral_link", null, null));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<AttributionResultDto>())!;
    }

    /// <summary>Create attribution + drive booking to completed so the attribution is 'earned'.</summary>
    private async Task EarnAsync(HttpClient super, Guid bookingId)
    {
        await CreateAttributionAsync(super, bookingId);
        await ApproveBookingAsync(super, bookingId);
        await CompleteBookingAsync(super, bookingId);
        Assert.Equal("earned", await AttrStatusAsync(bookingId));
    }

    /// <param name="raisedBy">DB CHECK: one of 'broker' | 'tenant_staff' | 'platform_audit'.</param>
    /// <param name="reason">DB CHECK: one of incorrect_attribution|duplicate_claim|pndt_violation|patient_dispute|fraud_suspected|commission_calculation_wrong|other.</param>
    private async Task<Guid> RaiseDisputeAsync(HttpClient c, Guid attributionId, string raisedBy, string reason)
    {
        var resp = await c.PostAsJsonAsync("/api/v1/commission/disputes",
            new RaiseDisputeRequest(attributionId, raisedBy, reason, $"QA dispute {reason}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<Guid>();
    }

    // ---- booking action POSTs (require Idempotency-Key: BookingActionCommand : IRequireIdempotency) --

    private static async Task<HttpResponseMessage> BookingActionAsync(HttpClient client, Guid bookingId, string action, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/bookings/{bookingId}/{action}")
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(req);
    }

    private static async Task ApproveBookingAsync(HttpClient c, Guid id)
    {
        var r = await BookingActionAsync(c, id, "approve");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static async Task CompleteBookingAsync(HttpClient c, Guid id)
    {
        var r = await BookingActionAsync(c, id, "complete");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static async Task CancelBookingAsync(HttpClient c, Guid id, string reason)
    {
        var r = await BookingActionAsync(c, id, "cancel", new { reason });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static async Task NoShowBookingAsync(HttpClient c, Guid id)
    {
        var r = await BookingActionAsync(c, id, "no-show");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ---- DB helpers (owner conn) ------------------------------------------------------------------

    /// <summary>Seeds a fresh COMPLETABLE booking (status 'pending', no discount) on its own slot, in the fixture tenant.</summary>
    private async Task<(Guid BookingId, Guid SlotId)> SeedCompletableBookingAsync(Guid? doctorId = null)
    {
        var doc = doctorId ?? factory.DoctorId;
        var slotId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        // Unique start time within the day (sub-second resolution) to avoid (doctor, slot_date, start_time)
        // collisions. Capped so start + 1min never wraps past midnight (which would make end_time < start_time).
        var ticksOfDay = DateTime.UtcNow.Ticks % (TimeSpan.TicksPerDay - TimeSpan.TicksPerMinute);
        var start = new TimeOnly(ticksOfDay);
        await ExecAsync("INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,@s,@e,'booked',1,1,NOW())",
            ("id", slotId), ("t", factory.TenantId), ("d", doc), ("s", start), ("e", start.Add(TimeSpan.FromMinutes(1))));
        await ExecAsync("INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'pending','dashboard','self',0,NOW(),NOW())",
            ("id", bookingId), ("t", factory.TenantId), ("s", slotId), ("p", factory.PatientId), ("d", doc));
        return (bookingId, slotId);
    }

    private static async Task CleanBookingAsync(Guid bookingId, Guid slotId)
    {
        await ExecAsync("DELETE FROM commission.attribution_disputes WHERE attribution_id IN (SELECT attribution_id FROM commission.attributions WHERE booking_id=@b)", ("b", bookingId));
        await ExecAsync("UPDATE commission.attributions SET payout_id=NULL WHERE booking_id=@b", ("b", bookingId));
        await ExecAsync("DELETE FROM commission.payouts WHERE payout_id IN (SELECT payout_id FROM commission.attributions WHERE booking_id=@b)", ("b", bookingId));
        await ExecAsync("DELETE FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));
        await ExecAsync("DELETE FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
        await ExecAsync("DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", slotId));
    }

    private static Task<int> SettleAsync() =>
        ScalarAsync<int>("SELECT commission.settle_earned_attributions(make_interval(secs => 0))::int");

    /// <summary>Earn → settle → batch → approve → execute, returning the now-PAID payout.</summary>
    private async Task<PayoutDto> DriveToPaidPayoutAsync(HttpClient super, HttpClient finance, Guid bookingId)
    {
        await EarnAsync(super, bookingId);
        await SettleAsync();
        var batchResp = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
            new CreatePayoutBatchRequest(factory.BrokerId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
        var payout = await batchResp.Content.ReadFromJsonAsync<PayoutDto>();
        await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });
        var exec = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
        Assert.Equal(HttpStatusCode.OK, exec.StatusCode);
        return payout;
    }

    private static Task SetTenantTanAsync(Guid tenantId, string? tan) =>
        ExecAsync("UPDATE platform.tenants SET tan=@v WHERE tenant_id=@t", ("t", tenantId), ("v", (object?)tan ?? DBNull.Value));

    /// <summary>Encrypts a PAN via the app's real field-encryption service and stores the envelope on the broker.</summary>
    private async Task SetBrokerPanAsync(Guid brokerId, string pan)
    {
        using var scope = factory.Services.CreateScope();
        var enc = scope.ServiceProvider.GetRequiredService<IFieldEncryptionService>();
        var env = await enc.EncryptAsync(new FieldRef("commission", "brokers", "pan_number"), factory.TenantId, pan,
            new EncryptionContext(factory.SuperUserId, factory.TenantId, "broker", brokerId, null), default);
        await ExecAsync("UPDATE commission.brokers SET pan_number=@e WHERE broker_id=@b", ("e", env), ("b", brokerId));
    }

    private static Task<string?> PayoutInvoiceNumberAsync(Guid payoutId) =>
        ScalarOrNullAsync<string>("SELECT invoice_number FROM commission.payouts WHERE payout_id=@p", ("p", payoutId));

    private static Task<string?> PayoutForm16AUrlAsync(Guid payoutId) =>
        ScalarOrNullAsync<string>("SELECT form_16a_url FROM commission.payouts WHERE payout_id=@p", ("p", payoutId));

    /// <summary>Seeds an ACTIVE campaign (window now±1d, no targeting) in the fixture tenant; returns its id.</summary>
    private async Task<Guid> SeedCampaignAsync(string bonusType, decimal bonusValue, decimal? budget = null, int? minBookings = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO commission.broker_campaigns
                (campaign_id, tenant_id, campaign_name, bonus_type, bonus_value, min_bookings_for_bonus,
                 starts_at, ends_at, is_active, total_budget_inr, spent_so_far_inr, created_at, updated_at)
            VALUES (@id, @t, 'QA Campaign', @bt, @bv, @mb, @s, @e, true, @budget, 0, NOW(), NOW())
            """,
            ("id", id), ("t", factory.TenantId), ("bt", bonusType), ("bv", bonusValue),
            ("mb", (object?)minBookings ?? DBNull.Value),
            ("s", DateTime.UtcNow.AddDays(-1)), ("e", DateTime.UtcNow.AddDays(1)),
            ("budget", (object?)budget ?? DBNull.Value));
        return id;
    }

    private static Task CleanCampaignAsync(Guid campaignId) =>
        ExecAsync("DELETE FROM commission.broker_campaigns WHERE campaign_id=@c", ("c", campaignId));

    private static Task<decimal> CampaignSpentAsync(Guid campaignId) =>
        ScalarAsync<decimal>("SELECT spent_so_far_inr FROM commission.broker_campaigns WHERE campaign_id=@c", ("c", campaignId));

    private static Task<decimal> AttrCommissionAsync(Guid bookingId) =>
        ScalarAsync<decimal>("SELECT commission_amount_inr FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));

    private static Task<decimal> AttrBonusAsync(Guid bookingId) =>
        ScalarAsync<decimal>("SELECT COALESCE((source_metadata->>'campaign_bonus_inr')::numeric, 0) FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));

    private static Task<Guid> AttrCampaignIdAsync(Guid bookingId) =>
        ScalarAsync<Guid>("SELECT (source_metadata->>'campaign_id')::uuid FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));

    private static Task<string?> AttrStatusAsync(Guid bookingId) =>
        ScalarOrNullAsync<string>("SELECT commission_status FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));

    private static Task<string?> PayoutStatusAsync(Guid payoutId) =>
        ScalarOrNullAsync<string>("SELECT status FROM commission.payouts WHERE payout_id=@p", ("p", payoutId));

    private static Task<Guid> AttrIdAsync(Guid bookingId) =>
        ScalarAsync<Guid>("SELECT COALESCE((SELECT attribution_id FROM commission.attributions WHERE booking_id=@b LIMIT 1), '00000000-0000-0000-0000-000000000000'::uuid)", ("b", bookingId));

    private static Task<bool> AttrEarnedAtSetAsync(Guid bookingId) =>
        ScalarAsync<bool>("SELECT earned_at IS NOT NULL FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));

    private async Task<(decimal Pending, decimal Earned)> PendingEarnedAsync() =>
        (await WalletAsync("pending_inr"), await WalletAsync("earned_inr"));

    private Task<decimal> WalletAsync(string column) =>
        ScalarAsync<decimal>($"SELECT {column} FROM commission.broker_wallets WHERE broker_id=@b", ("b", factory.BrokerId));

    /// <summary>
    /// Zeroes the shared broker wallet so this test's wallet-delta assertions are EXACT and decoupled from any
    /// residue a prior test left (e.g. a settled ready_to_pay balance whose attribution row was cleaned up).
    /// Only this test class touches this broker's wallet, and xUnit runs a class's tests sequentially, so a
    /// per-test reset is safe. The wallet is materialized state derived from attribution rows — never source of truth.
    /// </summary>
    private Task ResetWalletAsync() =>
        ExecAsync(
            """
            UPDATE commission.broker_wallets
            SET pending_inr=0, earned_inr=0, ready_to_pay_inr=0, lifetime_paid_inr=0, lifetime_reversed_inr=0,
                current_month_inr=0, current_month_attributions=0, lifetime_attributions=0
            WHERE broker_id=@b
            """, ("b", factory.BrokerId));

    private static async Task<(string? Reference, string? Gateway)> PayoutRefAndGatewayAsync(Guid payoutId)
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT payment_reference, payment_gateway FROM commission.payouts WHERE payout_id=@p", conn);
        cmd.Parameters.AddWithValue("p", payoutId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "payout row must exist");
        var reference = rd.IsDBNull(0) ? null : rd.GetString(0);
        var gateway = rd.IsDBNull(1) ? null : rd.GetString(1);
        return (reference, gateway);
    }

    private static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<T?> ScalarOrNullAsync<T>(string sql, params (string Name, object Value)[] ps) where T : class
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as T;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
