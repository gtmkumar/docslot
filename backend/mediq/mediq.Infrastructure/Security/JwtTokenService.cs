using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using mediq.Domain.Platform;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Issues HMAC-SHA256 access JWTs (sub/email/jti + active tenant) and opaque, hashed refresh tokens.
/// In production, swap the symmetric key for RS256/JWKS so the signing key never leaves the issuer.
/// </summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    public const string TenantClaim = "tenant_id";
    public const string TokenUseClaim = "token_use";   // "user" | "client"
    public const string ClientIdClaim = "client_id";
    public const string ScopeClaim = "scope";          // space-delimited (OAuth convention)
    public const string TokenUseClient = "client";
    public const string BrokerClaim = "broker_id";     // server-resolved broker identity for broker-role users

    private readonly JwtOptions _o = options.Value;

    public AccessToken CreateAccessToken(User user, Guid? activeTenantId, Guid? brokerId = null)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        if (activeTenantId is { } tid)
            claims.Add(new Claim(TenantClaim, tid.ToString()));
        if (brokerId is { } bid)
            claims.Add(new Claim(BrokerClaim, bid.ToString()));   // IDOR-safe: the only trusted broker identity

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: creds);

        var value = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessToken(value, expires, _o.AccessTokenMinutes * 60);
    }

    public AccessToken CreateClientAccessToken(Guid clientId, Guid? tenantId, IReadOnlyCollection<string> grantedScopes)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, clientId.ToString()),
            new(ClientIdClaim, clientId.ToString()),
            new(TokenUseClaim, TokenUseClient),
            new(ScopeClaim, string.Join(' ', grantedScopes)),   // OAuth space-delimited scope claim
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        if (tenantId is { } tid)
            claims.Add(new Claim(TenantClaim, tid.ToString()));

        var token = new JwtSecurityToken(
            issuer: _o.Issuer, audience: _o.Audience, claims: claims,
            notBefore: DateTime.UtcNow, expires: expires, signingCredentials: creds);

        var value = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessToken(value, expires, _o.AccessTokenMinutes * 60);
    }

    public (string Raw, string Hash) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return (raw, HashToken(raw));
    }

    public string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
