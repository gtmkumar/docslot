namespace mediq.SharedDataModel.Docslot.PlatformApi;

/// <summary>
/// OAuth 2.0 client-credentials token request. Mirrors <c>POST /api/v1/oauth/token</c>. <see cref="Scope"/>
/// is a space-delimited list per RFC 6749 §3.3 (e.g. <c>"docslot.bookings.read docslot.slots.read"</c>);
/// empty means "all scopes the client is granted".
/// </summary>
public sealed record OAuthTokenRequest(
    string GrantType,
    string ClientId,
    string ClientSecret,
    string? Scope = null,
    Guid? TenantId = null);

/// <summary>
/// RFC 6749 token response. <see cref="AccessToken"/> is a scoped JWT; <see cref="Scope"/> is the
/// space-delimited GRANTED scopes (may be a subset of requested). No refresh token for client-credentials.
/// </summary>
public sealed record OAuthTokenResponse(
    string AccessToken,
    string TokenType,          // "Bearer"
    int ExpiresIn,             // seconds
    string Scope);

/// <summary>RFC 7009 token revocation request (revokes by the presented access token).</summary>
public sealed record OAuthRevokeRequest(string Token);

/// <summary>One API scope from the registry (maps to <c>platform_api.api_scopes</c>).</summary>
public sealed record ScopeDto(
    string ScopeKey,
    string Resource,
    string Action,
    string Description,
    bool IsDangerous,
    bool RequiresConsent);
