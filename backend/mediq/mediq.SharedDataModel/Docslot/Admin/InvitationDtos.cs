namespace mediq.SharedDataModel.Docslot.Admin;

// ============================================================================
// Invitations (issue #89, epic #80 Phase C) — token-based tenant onboarding.
// A NEW capability alongside the direct-add invite (POST /tenants/{id}/users).
// The plaintext token is returned EXACTLY ONCE (create/resend) and never again;
// only its SHA-256 hash is persisted. List/read never expose the token or hash.
// ============================================================================

/// <summary>Mint an invitation. A password is never involved here — the invitee sets their own on accept.
/// <c>RoleId</c> (optional) is the role granted on accept; the actor may only attach a role they may confer
/// (R3 no-escalation, enforced at the DB).</summary>
public sealed record CreateInvitationRequest(string Email, Guid? RoleId = null);

/// <summary>Result of minting/resending an invitation. <c>Token</c> is the ONE-TIME plaintext — the caller must
/// hand it to the send step (#93) now; it is unrecoverable afterwards. Response is never idempotency-cached.</summary>
public sealed record InvitationTokenResult(
    Guid InvitationId, string Token, DateTime ExpiresAt, int ResendCount);

/// <summary>A single invitation row for the console list. NEVER carries the token or its hash.</summary>
public sealed record InvitationDto(
    Guid InvitationId, string InvitedEmail, Guid? RoleId, string? RoleName, string Status,
    DateTime ExpiresAt, int ResendCount, Guid? InvitedByUserId, Guid? AcceptedUserId,
    DateTime? AcceptedAt, DateTime? RevokedAt, DateTime CreatedAt);

/// <summary>The invitation list plus a <c>Count</c> for the tab badge.</summary>
public sealed record InvitationListDto(IReadOnlyList<InvitationDto> Items, int Count);

/// <summary>Result of revoking an invitation. <c>AlreadyInactive</c>=true when it was not pending (idempotent).</summary>
public sealed record RevokeInvitationResult(Guid InvitationId, bool AlreadyInactive);

/// <summary>Redeem an invitation. The token IS the authorization — no JWT. The invitee sets their own
/// <c>DisplayName</c> + <c>Password</c>. Single-use.</summary>
public sealed record AcceptInvitationRequest(string Token, string DisplayName, string Password);

/// <summary>Result of accepting. <c>AlreadyExisted</c>=true when the email matched a global identity and we only
/// linked the tenant role (never overwriting that user's profile/password).</summary>
public sealed record AcceptInvitationResult(Guid UserId, Guid TenantId, bool AlreadyExisted);
