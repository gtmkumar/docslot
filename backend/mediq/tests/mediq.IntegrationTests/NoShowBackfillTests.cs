using System.IdentityModel.Tokens.Jwt;
using mediq.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-16 verification: the proactive no-show backfill picks up a due booking, scores it via the AI sibling
/// under a VALID short-lived SERVICE token (token_use=service, fixed non-human subject, the seeded tenant), marks
/// the booking so it is never re-predicted, and is IDEMPOTENT (a second pass does not re-call the AI for it). The
/// due-list is cross-tenant, so all assertions are scoped to the seeded booking id to stay isolated on the shared DB.
/// </summary>
public sealed class NoShowBackfillTests(NoShowBackfillWebAppFactory factory) : IClassFixture<NoShowBackfillWebAppFactory>
{
    [Fact]
    public async Task Backfill_Predicts_Due_Booking_Mints_Valid_Service_Token_And_Is_Idempotent()
    {
        // --- Pass 1: the due booking is scored + marked --------------------------------------------------------
        using (var scope = factory.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<INoShowBackfillRunner>();
            await runner.RunOnceAsync(CancellationToken.None);
        }

        // Exactly ONE call for the seeded booking (the scan is cross-tenant; filter to our id for isolation).
        var callsForBooking = factory.Ai.Calls.Where(c => c.BookingId == factory.BookingId).ToList();
        Assert.Single(callsForBooking);

        // The features the worker derived for our booking (not on-behalf ⇒ is_behalf=false).
        Assert.False(callsForBooking[0].Features.IsBehalfBooking);

        // --- The captured bearer is a VALID service token ------------------------------------------------------
        var bearer = callsForBooking[0].ServiceBearer;
        Assert.False(string.IsNullOrWhiteSpace(bearer));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(bearer);
        Assert.Equal("svc:no-show-predictor", jwt.Subject);
        Assert.Equal("service", jwt.Claims.Single(c => c.Type == "token_use").Value);
        Assert.Equal(factory.TenantId.ToString(), jwt.Claims.Single(c => c.Type == "tenant_id").Value);
        // A service token carries NO scope/role/email — it confers no permissions beyond the AI's non-PHI path.
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "scope" or "roles" or "email");
        // Short-lived: still valid now, expiring within ~the TTL (clamped ≤ 15 min; assert ≤ 16 for clock slack).
        Assert.True(jwt.ValidTo > DateTime.UtcNow, "service token must not already be expired");
        Assert.True(jwt.ValidTo <= DateTime.UtcNow.AddMinutes(16), "service token TTL must be short");

        // --- The booking is now marked (idempotency marker set) ------------------------------------------------
        var predictedAt = await factory.ReadPredictedAtAsync();
        Assert.NotNull(predictedAt);

        // --- Pass 2: idempotent — the marked booking is no longer due, so NO new AI call for it -----------------
        using (var scope = factory.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<INoShowBackfillRunner>();
            await runner.RunOnceAsync(CancellationToken.None);
        }

        var callsAfterSecondPass = factory.Ai.Calls.Count(c => c.BookingId == factory.BookingId);
        Assert.Equal(1, callsAfterSecondPass);
    }
}
