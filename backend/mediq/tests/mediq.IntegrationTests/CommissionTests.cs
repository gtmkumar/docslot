using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Domain.Commission;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Commission;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-07 broker-economy invariants against the live canonical DB: PAN ciphertext-at-rest; attribution on
/// a discounted booking REJECTED (exclusivity trigger); PCPNDT CHECK rejects bad writes; payout TDS/GST/net
/// math + ₹100 floor; payout EXECUTE denied without the execute permission even when approve was granted
/// (approval≠execution); attribution UNIQUE per (booking,broker); events carry NO PHI.
/// </summary>
public sealed class CommissionTests(CommissionWebAppFactory factory) : IClassFixture<CommissionWebAppFactory>
{
    [Fact]
    public async Task Register_Broker_Encrypts_PAN_At_Rest()
    {
        var client = await ClientAsync(factory.SuperEmail);
        var phone = $"+9193{Random.Shared.Next(10000000, 99999999)}";
        const string pan = "ABCPK1234L";
        var resp = await client.PostAsJsonAsync("/api/v1/commission/brokers",
            new RegisterBrokerRequest(phone, "PAN Broker", null, "individual", pan, null));
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<RegisterBrokerResult>();

        // CIPHERTEXT AT REST: the raw pan_number column must NOT contain the plaintext PAN.
        var rawPan = await ScalarStrAsync("SELECT pan_number FROM commission.brokers WHERE broker_id=@id", ("id", result!.BrokerId));
        Assert.NotNull(rawPan);
        Assert.DoesNotContain(pan, rawPan!);

        await ExecAsync("DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", result.BrokerId));
        await ExecAsync("DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", result.BrokerId));
        await ExecAsync("DELETE FROM platform.key_usage_log WHERE key_id IN (SELECT key_id FROM platform.encryption_keys WHERE tenant_id=@t)", ("t", factory.TenantId));
        await ExecAsync("DELETE FROM commission.brokers WHERE broker_id=@b", ("b", result.BrokerId));
    }

    [Fact]
    public async Task Attribution_On_Clean_Booking_Succeeds_With_Commission()
    {
        var client = await ClientAsync(factory.SuperEmail);
        var resp = await client.PostAsJsonAsync("/api/v1/commission/attributions",
            new CreateAttributionRequest(factory.CleanBookingId, factory.BrokerId, "referral_link", null, null));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var attr = await resp.Content.ReadFromJsonAsync<AttributionResultDto>();
        Assert.Equal("auto_verified", attr!.VerificationStatus);
        Assert.Equal(200m, attr.CommissionAmountInr);   // the flat ₹200 rule

        // UNIQUE per (booking, broker): a second attribution by the same broker is rejected.
        var dup = await client.PostAsJsonAsync("/api/v1/commission/attributions",
            new CreateAttributionRequest(factory.CleanBookingId, factory.BrokerId, "referral_link", null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);

        await ExecAsync("DELETE FROM commission.attributions WHERE booking_id=@b", ("b", factory.CleanBookingId));
    }

    [Fact]
    public async Task Attribution_On_Discounted_Booking_Is_Rejected()
    {
        var client = await ClientAsync(factory.SuperEmail);
        // The discounted booking (direct_discount_inr=125) → DB exclusivity trigger → 422.
        var resp = await client.PostAsJsonAsync("/api/v1/commission/attributions",
            new CreateAttributionRequest(factory.DiscountedBookingId, factory.BrokerId, "post_hoc_claim", null, null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        // And nothing was written.
        var count = await ScalarIntAsync("SELECT COUNT(*)::int FROM commission.attributions WHERE booking_id=@b", ("b", factory.DiscountedBookingId));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task PCPNDT_Check_Rejects_Forbidden_Values()
    {
        // can_refer_pndt=true is forbidden by DB CHECK.
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(
            "UPDATE commission.brokers SET can_refer_pndt=true WHERE broker_id=@b", ("b", factory.BrokerId)));
        // excludes_pndt=false on a rule is forbidden by DB CHECK.
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(
            "UPDATE commission.commission_rules SET excludes_pndt=false WHERE rule_id=@r", ("r", factory.RuleId)));
    }

    [Fact]
    public void Payout_Math_TDS_GST_Net_And_Floor_Are_Correct()
    {
        // GST-registered: gross 1000 → TDS 5% (50) → GST 18% added (180) → net 1130.
        var registered = PayoutCalculator.Compute(1000m, brokerGstRegistered: true);
        Assert.Equal(50m, registered.TdsInr);
        Assert.Equal(180m, registered.GstInr);
        Assert.Equal(1130m, registered.NetInr);
        Assert.True(registered.MeetsMinimum);

        // Not registered: gross 1000 → TDS 50, no GST → net 950.
        var plain = PayoutCalculator.Compute(1000m, brokerGstRegistered: false);
        Assert.Equal(50m, plain.TdsInr);
        Assert.Equal(0m, plain.GstInr);
        Assert.Equal(950m, plain.NetInr);

        // Below the ₹100 floor: gross 80 (not registered) → net 76 → does NOT meet minimum.
        var tiny = PayoutCalculator.Compute(80m, brokerGstRegistered: false);
        Assert.False(tiny.MeetsMinimum);
    }

    [Fact]
    public async Task Payout_Execute_Denied_Without_Execute_Permission_Even_If_Approve_Granted()
    {
        // Make a ready-to-pay attribution so a payout batch can be created (gross ≥ ₹100).
        await ExecAsync(
            """
            INSERT INTO commission.attributions (attribution_id, tenant_id, booking_id, broker_id, attribution_source,
                verification_status, commission_amount_inr, commission_status, attributed_at, earned_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @t, @b, @br, 'referral_link', 'auto_verified', 500.00, 'ready_to_pay', NOW(), NOW(), NOW(), NOW())
            """,
            ("t", factory.TenantId), ("b", factory.CleanBookingId), ("br", factory.BrokerId));

        // The FINANCE user (tenant_admin) HAS commission.payouts.approve but NOT commission.payouts.execute.
        var finance = await ClientAsync(factory.FinanceEmail);
        var batch = await finance.PostAsJsonAsync("/api/v1/commission/payouts/batch",
            new CreatePayoutBatchRequest(factory.BrokerId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)));
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var payout = await batch.Content.ReadFromJsonAsync<PayoutDto>();

        // Approve succeeds (finance has approve).
        var approve = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout!.PayoutId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // EXECUTE is DENIED (403) — finance lacks commission.payouts.execute (approval≠execution).
        var execDenied = await finance.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
        Assert.Equal(HttpStatusCode.Forbidden, execDenied.StatusCode);

        // The SUPER user (has execute) CAN execute.
        var super = await ClientAsync(factory.SuperEmail);
        var execOk = await super.PostAsJsonAsync($"/api/v1/commission/payouts/{payout.PayoutId}/execute", new { });
        Assert.Equal(HttpStatusCode.OK, execOk.StatusCode);
        var result = await execOk.Content.ReadFromJsonAsync<PayoutActionResult>();
        Assert.Equal("paid", result!.Status);

        await ExecAsync("UPDATE commission.attributions SET payout_id=NULL WHERE booking_id=@b", ("b", factory.CleanBookingId));
        await ExecAsync("DELETE FROM commission.attributions WHERE booking_id=@b", ("b", factory.CleanBookingId));
    }

    [Fact]
    public async Task Broker_Self_Service_Confined_To_Own_Broker_Identity()
    {
        // Broker A logs in → token carries broker_id = BrokerId (server-resolved from commission.brokers.user_id).
        var brokerA = await ClientAsync(factory.BrokerAEmail);

        // me/wallet returns broker A's OWN wallet (the endpoint uses the claim; there is NO brokerId param to spoof).
        var wallet = await brokerA.GetAsync("/api/v1/commission/me/wallet");
        Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
        var dto = await wallet.Content.ReadFromJsonAsync<BrokerWalletDto>();
        Assert.Equal(factory.BrokerId, dto!.BrokerId);            // OWN broker, never broker B
        Assert.NotEqual(factory.BrokerBId, dto.BrokerId);

        // Creating a link applies to broker A only (claim-derived); broker B is never reachable.
        var link = await brokerA.PostAsJsonAsync("/api/v1/commission/me/links", new CreateReferralLinkRequest(factory.TenantId, null, "campaign"));
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var createdForA = await ScalarIntAsync("SELECT COUNT(*)::int FROM commission.referral_links WHERE broker_id=@b", ("b", factory.BrokerId));
        Assert.True(createdForA >= 1);
        var createdForB = await ScalarIntAsync("SELECT COUNT(*)::int FROM commission.referral_links WHERE broker_id=@b", ("b", factory.BrokerBId));
        Assert.Equal(0, createdForB);                            // nothing ever created under broker B

        // A non-broker user (super_admin, no broker_id claim) hitting a self-service endpoint → 403.
        var nonBroker = await ClientAsync(factory.SuperEmail);
        var denied = await nonBroker.GetAsync("/api/v1/commission/me/wallet");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await ExecAsync("DELETE FROM commission.referral_links WHERE broker_id=@b", ("b", factory.BrokerId));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<HttpClient> ClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, CommissionWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<int> ScalarIntAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStrAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
