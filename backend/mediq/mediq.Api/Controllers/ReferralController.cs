using System.Security.Cryptography;
using System.Text;
using mediq.Application.Cqrs;
using mediq.Application.Features.Commission;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Public referral-link landing (slice 07 / Phase-2 attribution path #3). A broker shares
/// <c>/api/v1/ref/{shortCode}</c>; a patient clicking it has the click logged (IP hashed, UA trimmed — no raw
/// IP, no PHI) and is 302-redirected to the clinic's WhatsApp with the referral code prefilled. The patient's
/// first WhatsApp message carries the code, which the inbound handler detects to attribute the booking to the
/// broker. The redirect target is OUR stored <c>target_url</c> (a wa.me deep link we built), never client input
/// — no open-redirect. Anonymous + tenant-less (referral tables carry no PHI and no RLS).
/// </summary>
[ApiController]
[Route("api/v1/ref")]
[AllowAnonymous]
public sealed class ReferralController(ICommandDispatcher commands, IConfiguration config) : ControllerBase
{
    private const string SessionCookie = "ds_ref_sess";

    [HttpGet("{shortCode}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Click(string shortCode, CancellationToken ct)
    {
        // Privacy: persist only a KEYED HMAC-SHA256 of the IP (never the raw IP, and not a bare SHA-256 — the
        // IPv4 space is small enough to rainbow-table) and a trimmed UA. No PHI. The key is server-side config
        // (a dev default here; a real secret is injected per environment, never committed).
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ipKey = Encoding.UTF8.GetBytes(config["Referral:IpHashKey"] ?? "docslot-referral-ip-hmac-dev-key");
        var ipHash = string.IsNullOrEmpty(ip)
            ? null
            : Convert.ToHexString(HMACSHA256.HashData(ipKey, Encoding.UTF8.GetBytes(ip))).ToLowerInvariant();
        var ua = Request.Headers.UserAgent.ToString();
        var uaBrief = string.IsNullOrEmpty(ua) ? null : (ua.Length > 50 ? ua[..50] : ua);

        // A lightweight visitor session (cookie) so repeat clicks from the same browser correlate (analytics).
        var session = Request.Cookies[SessionCookie];
        if (string.IsNullOrEmpty(session))
        {
            session = Guid.NewGuid().ToString("N");
            Response.Cookies.Append(SessionCookie, session, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(30),
            });
        }

        var target = await commands.Send(new LogReferralClickCommand(shortCode, ipHash, uaBrief, session), ct);
        return target is null ? NotFound() : Redirect(target);
    }
}
