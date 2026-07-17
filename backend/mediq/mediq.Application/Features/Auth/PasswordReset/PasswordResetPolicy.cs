using mediq.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Auth.PasswordReset;

/// <summary>How long a freshly-minted password-reset token stays valid.</summary>
internal static class PasswordResetPolicy
{
    /// <summary>Short-lived: a reset link is a live credential (the DB default is also 1 hour).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
}

/// <summary>Builds the one-time reset link from the plaintext token. The base is configurable
/// (<c>PasswordReset:ResetUrlBase</c>); the default is a relative <c>/reset-password?token=...</c> that the SPA
/// resolves against its own origin (mirrors how the invite link is handed off relative).</summary>
internal static class PasswordResetLink
{
    public static string Build(IConfiguration config, string token)
    {
        var baseUrl = (config["PasswordReset:ResetUrlBase"] ?? string.Empty).TrimEnd('/');
        return $"{baseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
    }
}

/// <summary>ADVISORY dispatch of the reset link through <see cref="IPasswordResetNotifier"/>. Best-effort and
/// NON-BLOCKING: any notifier failure is swallowed + logged (warning) so the reset still commits. The token is
/// a live credential — never logged here.</summary>
internal static class PasswordResetNotify
{
    public static async Task AdvisoryAsync(
        IPasswordResetNotifier notifier, ILogger logger, PasswordResetNotification notification, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(notification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token was already minted — delivery is advisory, so a notifier failure must NOT surface to
            // the caller. Log WITHOUT the token/link.
            logger.LogWarning(ex,
                "Password-reset notifier failed for user={UserId} (advisory — token still minted)",
                notification.UserId);
        }
    }
}
