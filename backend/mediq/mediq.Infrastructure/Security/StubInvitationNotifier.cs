using mediq.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Dev/default invitation transport (issue #93). Does NOT send any email/WhatsApp — it RECORDS the intended
/// send so the whole invite-send path is exercisable end-to-end without an email/WhatsApp credential, then
/// returns. Selected whenever no live notifier provider is configured (<c>Invitations:NotifierProvider</c>
/// unset/none), mirroring <see cref="Docslot.WhatsApp.StubWhatsAppSender"/>.
/// <para>
/// PHI/secret hygiene: the one-time token/link is a LIVE CREDENTIAL — it is logged ONLY at
/// <see cref="LogLevel.Debug"/> (never at info/prod level). The info-level trace carries just the masked email,
/// tenant, and intent so an operator can confirm a send was attempted without leaking the credential.
/// </para>
/// </summary>
public sealed class StubInvitationNotifier(ILogger<StubInvitationNotifier> logger) : IInvitationNotifier
{
    public Task NotifyAsync(InvitationNotification notification, CancellationToken ct)
    {
        // Info level: intent + tenant + MASKED email only — never the token/link.
        logger.LogInformation(
            "StubInvitationNotifier: simulated invite {Kind} invitation={InvitationId} tenant={TenantId} to={Email} (no live delivery)",
            notification.IsResend ? "resend" : "send", notification.InvitationId, notification.TenantId,
            MaskEmail(notification.InvitedEmail));

        // Debug ONLY: the token is a live credential. This never fires at prod/info level.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(
                "StubInvitationNotifier: invitation={InvitationId} token(len)={TokenLength} expires={ExpiresAt:o}",
                notification.InvitationId, notification.Token.Length, notification.ExpiresAt);

        return Task.CompletedTask;
    }

    /// <summary>Mask the local-part so logs never carry the full invitee address (e.g. <c>a***@x.com</c>).</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var shown = local.Length <= 1 ? local : local[0].ToString();
        return shown + new string('*', Math.Max(1, local.Length - 1)) + email[at..];
    }
}
