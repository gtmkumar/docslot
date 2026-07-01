using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Abstractions;

/// <summary>
/// Generates the invitation's one-time bearer token and its at-rest hash. The plaintext is returned to the
/// caller ONCE; only <see cref="Hash"/> of it is ever persisted (SHA-256, constant-time compared on accept).
/// Lives behind an abstraction so the crypto stays in Infrastructure and is swappable/testable.
/// </summary>
public interface IInvitationTokenFactory
{
    /// <summary>Mints a fresh, high-entropy URL-safe token and returns it alongside its SHA-256 hash.</summary>
    (string Token, string TokenHash) Create();

    /// <summary>SHA-256 hash of a presented plaintext token (for the accept lookup).</summary>
    string Hash(string token);
}

/// <summary>
/// Write + read access to <c>platform.invitations</c>. All writes travel the SECURITY DEFINER functions
/// (create/resend/revoke enforce the actor's <c>tenant.users.create</c> + R3 no-escalation; accept is
/// unauthenticated — the token is the authorization). SQLSTATE 42501 → <see cref="Utilities.Exceptions.ForbiddenException"/>,
/// 23505 (duplicate live pending) → <see cref="Utilities.Exceptions.ConflictException"/>, P0002 (no acceptable
/// row) → a generic <see cref="Utilities.Exceptions.BusinessRuleException"/> so accept never enumerates.
/// </summary>
public interface IInvitationRepository
{
    /// <summary>Calls <c>platform.create_invitation</c>; returns the new invitation_id.</summary>
    Task<Guid> CreateAsync(
        Guid actorUserId, Guid tenantId, string invitedEmail, Guid? roleId, string tokenHash,
        DateTime expiresAt, CancellationToken ct);

    /// <summary>Calls <c>platform.resend_invitation</c> (rotate token + extend expiry, bump count); returns the id.</summary>
    Task<Guid> ResendAsync(
        Guid actorUserId, Guid tenantId, Guid invitationId, string newTokenHash, DateTime newExpiresAt, CancellationToken ct);

    /// <summary>Calls <c>platform.revoke_invitation</c>; false when it was not pending (idempotent).</summary>
    Task<bool> RevokeAsync(Guid actorUserId, Guid tenantId, Guid invitationId, CancellationToken ct);

    /// <summary>Calls <c>platform.accept_invitation</c>; provisions/links the user + assigns the pre-vetted role.</summary>
    Task<(Guid UserId, Guid TenantId, bool AlreadyExisted)> AcceptAsync(
        string tokenHash, string passwordHash, string displayName, CancellationToken ct);

    /// <summary>Lists invitations for a tenant (optionally filtered by status). Never returns the token/hash.
    /// Tenant scoping is enforced by RLS (invitations_read) + the explicit predicate.</summary>
    Task<IReadOnlyList<InvitationDto>> ListAsync(Guid tenantId, string? status, CancellationToken ct);
}

/// <summary>
/// Delivers a freshly-minted invitation link/token to the invitee (issue #93, epic #80 Phase D). Sits behind
/// an OFFLINE PROVIDER SEAM exactly like <see cref="IWhatsAppSender"/>: the dev/default
/// <c>StubInvitationNotifier</c> RECORDS the intended send (observable/testable) but performs NO live delivery;
/// a real email/WhatsApp transport is wired ONLY when its credentials are configured
/// (<c>Invitations:NotifierProvider</c> + the provider secret). The dispatch is ADVISORY / NON-BLOCKING — the
/// create/resend handlers swallow any failure so the one-time token is still returned to the admin. The raw
/// token/link is NEVER logged at info/prod level (debug-only, if at all): it is a live credential.
/// </summary>
public interface IInvitationNotifier
{
    /// <summary>Best-effort delivery of the invite link to the invitee. Implementations must not throw for a
    /// transient failure on the offline path; the caller treats this as advisory regardless.</summary>
    Task NotifyAsync(InvitationNotification notification, CancellationToken ct);
}

/// <summary>
/// The payload handed to <see cref="IInvitationNotifier"/>: who to reach (<paramref name="InvitedEmail"/>),
/// the tenant context, and the ONE-TIME plaintext <paramref name="Token"/> (from which the notifier builds the
/// accept link). <paramref name="IsResend"/> lets the transport pick a "reminder" vs "new invite" template.
/// The token is a live credential — never persist or log it.
/// </summary>
public sealed record InvitationNotification(
    Guid InvitationId,
    Guid TenantId,
    string InvitedEmail,
    string Token,
    DateTime ExpiresAt,
    bool IsResend);
