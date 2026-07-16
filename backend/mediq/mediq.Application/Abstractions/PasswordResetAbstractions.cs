namespace mediq.Application.Abstractions;

/// <summary>
/// Generates a password-reset one-time bearer token and its at-rest SHA-256 hash. Sibling to
/// <see cref="IInvitationTokenFactory"/> (identical crypto, distinct type so the auth-reset surface stays
/// self-contained). The plaintext leaves the process exactly once (self-service: via the notifier; admin: in
/// the response body); only the hash is persisted, so a leaked at-rest row cannot reconstruct the token.
/// </summary>
public interface IPasswordResetTokenFactory
{
    /// <summary>Mints a fresh, high-entropy URL-safe token and returns it alongside its SHA-256 hash.</summary>
    (string Token, string TokenHash) Create();

    /// <summary>SHA-256 hash of a presented plaintext token (for the consume lookup).</summary>
    string Hash(string token);
}

/// <summary>
/// Write + read access to <c>platform.password_reset_tokens</c> via the schema's SECURITY DEFINER functions
/// (database/12_password_reset.sql). RLS/least-privilege blocks direct app-role writes; the definer functions
/// are the only sanctioned path — <c>request_password_reset</c>/<c>admin_request_password_reset</c> mint
/// (the admin variant enforces the actor's <c>tenant.users.update</c> + the R3 no-escalation guard →
/// SQLSTATE 42501 → <see cref="Utilities.Exceptions.ForbiddenException"/>), and <c>consume_password_reset</c>
/// redeems unauthenticated (the token IS the authorization; P0002 → a generic
/// <see cref="Utilities.Exceptions.BusinessRuleException"/> so redemption never enumerates).
/// </summary>
public interface IPasswordResetRepository
{
    /// <summary>Calls <c>platform.request_password_reset</c> (self-service mint); returns the new token_id.</summary>
    Task<Guid> RequestAsync(
        Guid userId, string tokenHash, string? requestedIp, DateTime expiresAt, CancellationToken ct);

    /// <summary>Calls <c>platform.admin_request_password_reset</c> (admin-initiated, escalation-guarded);
    /// returns the new token_id. <paramref name="tenantId"/> is null for the platform (super_admin) route.</summary>
    Task<Guid> AdminRequestAsync(
        Guid actorUserId, Guid targetUserId, string tokenHash, string? requestedIp, DateTime expiresAt,
        Guid? tenantId, CancellationToken ct);

    /// <summary>Calls <c>platform.consume_password_reset</c>; sets the new hash, clears must_change_password +
    /// lockout, marks the token used, revokes active sessions. Returns the user_id. Any invalid/expired/used
    /// token surfaces as a generic <see cref="Utilities.Exceptions.BusinessRuleException"/> (no enumeration).</summary>
    Task<Guid> ConsumeAsync(string tokenHash, string passwordHash, CancellationToken ct);
}

/// <summary>
/// Delivers a freshly-minted password-reset link to the user. Sibling to <see cref="IInvitationNotifier"/> and
/// behind the same OFFLINE PROVIDER SEAM: the dev/default <c>StubPasswordResetNotifier</c> RECORDS the intended
/// send (a MASKED-email info log; the token only at Debug) but performs NO live delivery. Dispatch is ADVISORY /
/// NON-BLOCKING — the self-service handler swallows any failure. The raw token/link is NEVER logged at
/// info/prod level: it is a live credential.
/// </summary>
public interface IPasswordResetNotifier
{
    /// <summary>Best-effort delivery of the reset link. Must not throw for a transient failure on the offline
    /// path; the caller treats this as advisory regardless.</summary>
    Task NotifyAsync(PasswordResetNotification notification, CancellationToken ct);
}

/// <summary>
/// The payload handed to <see cref="IPasswordResetNotifier"/>: who to reach (<paramref name="Email"/>) and the
/// ONE-TIME plaintext <paramref name="Token"/> (from which the notifier builds the reset link). The token is a
/// live credential — never persist or log it. <paramref name="IsAdminInitiated"/> lets a transport pick an
/// "an administrator reset your password" vs "you requested a reset" template.
/// </summary>
public sealed record PasswordResetNotification(
    Guid UserId,
    string Email,
    string Token,
    DateTime ExpiresAt,
    bool IsAdminInitiated);
