using mediq.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Dev/default password-reset transport. Does NOT send any email/WhatsApp — it RECORDS the intended send so
/// the whole reset-send path is exercisable end-to-end without a delivery credential, then returns. Selected
/// whenever no live notifier provider is configured (<c>PasswordReset:NotifierProvider</c> unset/none),
/// mirroring <see cref="StubInvitationNotifier"/>.
/// <para>
/// PHI/secret hygiene: the one-time token/link is a LIVE CREDENTIAL — it is logged ONLY at
/// <see cref="LogLevel.Debug"/> (never at info/prod level). The info-level trace carries just the masked email
/// and intent so an operator can confirm a send was attempted without leaking the credential.
/// </para>
/// </summary>
public sealed class StubPasswordResetNotifier(ILogger<StubPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public Task NotifyAsync(PasswordResetNotification notification, CancellationToken ct)
    {
        // Info level: intent + MASKED email only — never the token/link.
        logger.LogInformation(
            "StubPasswordResetNotifier: simulated password-reset {Kind} to={Email} (no live delivery)",
            notification.IsAdminInitiated ? "admin-initiated" : "self-service",
            MaskEmail(notification.Email));

        // Debug ONLY: the token is a live credential. This never fires at prod/info level.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "StubPasswordResetNotifier: user={UserId} token(len)={TokenLength} expires={ExpiresAt:o}",
                notification.UserId, notification.Token.Length, notification.ExpiresAt);

        return Task.CompletedTask;
    }

    /// <summary>Mask the local-part so logs never carry the full address (e.g. <c>a***@x.com</c>).</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var shown = local.Length <= 1 ? local : local[0].ToString();
        return shown + new string('*', Math.Max(1, local.Length - 1)) + email[at..];
    }
}
