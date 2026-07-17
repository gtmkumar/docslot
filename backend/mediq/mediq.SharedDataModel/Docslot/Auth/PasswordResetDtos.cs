namespace mediq.SharedDataModel.Docslot.Auth;

// ============================================================================
// Password reset — self-service ("forgot password") + admin-initiated. Mirrors
// the invitation token flow but for an EXISTING user's credential. A one-time,
// hashed token is minted into platform.password_reset_tokens; the USER always
// sets the new password (an admin never handles plaintext). Only the token's
// SHA-256 hash is persisted — the plaintext is returned exactly once (admin
// route: in the response body; self-service: only via the offline notifier).
// ============================================================================

/// <summary>
/// Self-service reset request (<c>POST /api/v1/auth/forgot-password</c>). Anti-enumeration: the endpoint
/// ALWAYS returns <see cref="ForgotPasswordResult"/> with <c>Requested=true</c> whether or not the email maps
/// to a live account — the response never reveals whether the address exists.
/// </summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>Always-true acknowledgement for <c>POST /api/v1/auth/forgot-password</c> (see anti-enumeration note).</summary>
public sealed record ForgotPasswordResult(bool Requested);

/// <summary>
/// Redeem a reset token (<c>POST /api/v1/auth/reset-password</c>). The token IS the authorization (no JWT).
/// The new password must clear the platform floor (>=8, &lt;=128). An invalid / expired / already-used token
/// yields one generic failure (no enumeration).
/// </summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>Acknowledgement that the password was set for <c>POST /api/v1/auth/reset-password</c>.</summary>
public sealed record ResetPasswordResult(bool Reset);

/// <summary>
/// Result of an admin-initiated reset (<c>POST /tenants/{tenantId}/users/{userId}/reset-password</c> and the
/// platform variant). <see cref="ResetLink"/> is the ONE-TIME link the admin hands to the user out-of-band —
/// a LIVE CREDENTIAL returned in the response body ONLY (never logged), mirroring how create-invitation
/// returns the invite link. The user completes the reset via the self-service reset-password endpoint.
/// </summary>
public sealed record AdminResetPasswordResult(string ResetLink, DateTimeOffset ExpiresAt);
