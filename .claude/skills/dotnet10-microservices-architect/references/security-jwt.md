# Security — JWT, Refresh Tokens, Role-Based Authorization

The Identity service (microservice 3) issues access + refresh tokens. The gateway validates access tokens
at the edge; every service validates again (defense in depth). `ICurrentUser` exposes the authenticated
principal to the application layer (used by audit, see `references/cross-cutting.md`).

## Contents
- [Token model](#token-model)
- [Issuing access + refresh tokens](#issuing-access--refresh-tokens)
- [Refresh flow with rotation](#refresh-flow-with-rotation)
- [Validation in each service](#validation-in-each-service)
- [Role-based authorization](#role-based-authorization)
- [ICurrentUser](#icurrentuser)

## Token model

- **Access token**: short-lived (10–15 min) JWT, stateless, carries `sub`, `email`, `role` claims. Used
  on every request; validated by signature/issuer/audience/expiry. Never stored server-side.
- **Refresh token**: long-lived (e.g. 7–30 days) opaque random value, **stored hashed** in the Identity
  DB and bound to the user + device. Used only at `/api/auth/refresh` to mint a new access token.
  Rotated on every use.

> Storing refresh tokens hashed (not plaintext) means a DB leak doesn't hand an attacker live sessions.
> Rotation + reuse detection means a stolen refresh token is usable at most once before the chain is
> revoked.

## Issuing access + refresh tokens

```csharp
// Identity.Infrastructure/Security/JwtTokenService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _o = options.Value;

    public string CreateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r.Name)));

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // Opaque, cryptographically random refresh token. Return raw to client; store only the hash.
    public (string Raw, string Hash) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var raw = Convert.ToBase64String(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return (raw, hash);
    }
}
```

```csharp
// Identity.Application/Commands/Login/LoginCommandHandler.cs
public sealed class LoginCommandHandler(
    IUserRepository users, IJwtTokenService tokens, IRefreshTokenStore refreshStore)
    : ICommandHandler<LoginCommand, AuthResult>
{
    public async Task<AuthResult> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(cmd.Email, ct)
            ?? throw new InvalidCredentialsException();
        if (!user.VerifyPassword(cmd.Password))
            throw new InvalidCredentialsException();

        var access = tokens.CreateAccessToken(user);
        var (rawRefresh, hash) = tokens.CreateRefreshToken();
        await refreshStore.SaveAsync(user.Id, hash, cmd.DeviceId,
            expiresUtc: DateTime.UtcNow.AddDays(14), ct);

        return new AuthResult(access, rawRefresh, ExpiresInSeconds: 900);
    }
}
```

## Refresh flow with rotation

On refresh: look up the hashed token, verify it's unexpired and not revoked, **rotate** (revoke old,
issue new), and detect reuse — if a revoked token is presented, revoke the whole chain (a sign of theft).

```csharp
// Identity.Application/Commands/Refresh/RefreshCommandHandler.cs
public sealed class RefreshCommandHandler(
    IUserRepository users, IJwtTokenService tokens, IRefreshTokenStore store)
    : ICommandHandler<RefreshCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RefreshCommand cmd, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cmd.RefreshToken)));
        var stored = await store.FindAsync(hash, ct) ?? throw new InvalidRefreshTokenException();

        if (stored.RevokedUtc is not null)
        {
            // Reuse of a revoked token → compromise. Nuke the whole device chain.
            await store.RevokeChainAsync(stored.UserId, stored.DeviceId, ct);
            throw new InvalidRefreshTokenException();
        }
        if (stored.ExpiresUtc < DateTime.UtcNow) throw new InvalidRefreshTokenException();

        var user = await users.GetByIdAsync(stored.UserId, ct)!;
        var access = tokens.CreateAccessToken(user!);
        var (rawNew, hashNew) = tokens.CreateRefreshToken();

        await store.RotateAsync(oldHash: hash, newHash: hashNew,
            newExpiresUtc: DateTime.UtcNow.AddDays(14), ct);

        return new AuthResult(access, rawNew, ExpiresInSeconds: 900);
    }
}
```

```csharp
// Identity.Api/Controllers/AuthController.cs — anonymous routes (gateway lets these through).
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(ICommandDispatcher commands) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginCommand cmd, CancellationToken ct)
        => Ok(await commands.Send(cmd, ct));

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshCommand cmd, CancellationToken ct)
        => Ok(await commands.Send(cmd, ct));
}
```

## Validation in each service

Each `*.Api` validates the access token with the **same** parameters as the gateway. Centralize it in a
`BuildingBlocks.Web` extension so all services and the gateway share one definition — drift here is a
security hole.

```csharp
// BuildingBlocks.Web/Security/JwtExtensions.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

public static class JwtExtensions
{
    public static IServiceCollection AddPlatformJwtAuth(
        this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection("Jwt").Get<JwtOptions>()!;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                    ValidateAudience = true, ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                };
            });
        return services;
    }
}
```

> In production, prefer asymmetric signing (RS256) with the Identity service holding the private key and
> services/gateway validating with the public key from a JWKS endpoint — then the signing key never leaves
> Identity. The symmetric `HmacSha256` shown keeps the example self-contained; swap `SymmetricSecurityKey`
> for an `RsaSecurityKey` / JWKS resolver when you harden.

## Role-based authorization

Roles ride in the token as `ClaimTypes.Role`. Authorize per endpoint declaratively; define named policies
for anything beyond a simple role check.

```csharp
[Authorize(Roles = "admin")]
[HttpDelete("{id:guid}")]
public Task<IActionResult> Delete(Guid id, CancellationToken ct) => /* ... */;

// Policy for composite rules:
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("can-refund", p => p.RequireRole("admin", "support")
                                   .RequireClaim("permission", "orders:refund"));
```

## ICurrentUser

Application/audit code shouldn't reach into `HttpContext`. Expose the principal through an abstraction
defined in Application and implemented in the API layer over `IHttpContextAccessor`.

```csharp
// BuildingBlocks.Domain (or Application abstractions): the contract
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsInRole(string role);
}

// BuildingBlocks.Web/Security/CurrentUser.cs: the implementation
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? User?.FindFirst("sub")?.Value, out var id) ? id : null;

    public string? Email => User?.FindFirst(ClaimTypes.Email)?.Value;
    public bool IsInRole(string role) => User?.IsInRole(role) ?? false;
}
// Register: services.AddHttpContextAccessor(); services.AddScoped<ICurrentUser, CurrentUser>();
```

This is what the `AuditInterceptor` and audit-trail behavior consume to stamp "who" without coupling
persistence to the web layer.
