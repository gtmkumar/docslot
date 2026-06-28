using mediq.Application.Abstractions;
using mediq.Application.Cqrs;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Logs a public referral-link click and returns where to redirect the visitor. ANONYMOUS + tenant-less: the
/// referral tables carry no PHI and no RLS, so this runs without a tenant scope. The caller (the public
/// controller) pre-hashes the IP and trims the user-agent — no raw IP or full UA is ever persisted (privacy).
/// Returns the link's <c>target_url</c> (a WhatsApp deep link with the code prefilled) or null if the code is
/// unknown/inactive/expired (→ 404, no open redirect).
/// </summary>
public sealed record LogReferralClickCommand(string ShortCode, string? IpHash, string? UserAgentBrief, string? SessionToken)
    : ICommand<string?>;

public sealed class LogReferralClickCommandHandler(IReferralLinkRepository links, IClock clock)
    : ICommandHandler<LogReferralClickCommand, string?>
{
    public async Task<string?> Handle(LogReferralClickCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var link = await links.ResolveActiveByShortCodeAsync(command.ShortCode, now, ct);
        if (link is null)
            return null;   // unknown/inactive/expired code → caller returns 404 (never redirect to an arbitrary URL)

        await links.LogClickAsync(link.LinkId, command.ShortCode, command.SessionToken, command.IpHash,
            command.UserAgentBrief, referrerSource: "referral_link", now, ct);

        // Fall back to a bare WhatsApp link if a link somehow has no stored target (older rows).
        return string.IsNullOrWhiteSpace(link.TargetUrl) ? "https://wa.me/" : link.TargetUrl;
    }
}
