# YARP API Gateway — Routing, Edge JWT, Rate Limiting, Swagger Aggregation

`Gateway.Yarp` is **service 4** and the trust boundary. It reverse-proxies to the three business services,
validates JWTs before forwarding, enforces rate limits, stamps correlation IDs, and aggregates the
downstream OpenAPI documents. It contains no business logic and references no business projects.

## Contents

- [Program.cs](#programcs)
- [Routes & clusters config](#routes--clusters-config)
- [JWT at the edge](#jwt-at-the-edge)
- [Rate limiting](#rate-limiting)
- [Correlation ID forwarding](#correlation-id-forwarding)
- [Swagger aggregation](#swagger-aggregation)

## Program.cs

```csharp
// Gateway.Yarp/Program.cs
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();   // Aspire: OTel, health, discovery — see aspire-apphost.md

// YARP reads routes/clusters from config (appsettings.json + Aspire-injected service URLs).
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();   // resolve cluster addresses via Aspire discovery

// Edge JWT validation (same key/issuer as Identity service — see security-jwt.md).
builder.Services.AddGatewayJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

// Rate limiting partitioned per authenticated user (fallback to IP).
builder.Services.AddRateLimiter(ConfigureRateLimiter);

var app = builder.Build();

app.UseCorrelationId();          // from BuildingBlocks.Web — generate/propagate X-Correlation-ID
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapReverseProxy();           // YARP terminal middleware
app.MapDefaultEndpoints();       // /health, /alive from ServiceDefaults
app.Run();
```

## Routes & clusters config

YARP route table. In Aspire, destination addresses use the `http://{service-name}` scheme that service
discovery resolves; in standalone config you'd hardcode URLs. Auth is enforced per-route via
`AuthorizationPolicy`.

```jsonc
// Gateway.Yarp/appsettings.json
{
  "ReverseProxy": {
    "Routes": {
      "ordering-route": {
        "ClusterId": "ordering-cluster",
        "AuthorizationPolicy": "authenticated",
        "RateLimiterPolicy": "per-user",
        "Match": { "Path": "/api/orders/{**catch-all}" },
      },
      "catalog-route": {
        "ClusterId": "catalog-cluster",
        "AuthorizationPolicy": "authenticated",
        "RateLimiterPolicy": "per-user",
        "Match": { "Path": "/api/catalog/{**catch-all}" },
      },
      "identity-route": {
        "ClusterId": "identity-cluster",
        "AuthorizationPolicy": "anonymous", // login/refresh must be reachable unauthenticated
        "RateLimiterPolicy": "auth-endpoints",
        "Match": { "Path": "/api/auth/{**catch-all}" },
      },
    },
    "Clusters": {
      "ordering-cluster": {
        "Destinations": { "d1": { "Address": "http://opretion-api" } },
      },
      "catalog-cluster": {
        "Destinations": { "d1": { "Address": "http://core-api" } },
      },
      "identity-cluster": {
        "Destinations": { "d1": { "Address": "http://identity-api" } },
      },
    },
  },
}
```

> `http://ordering-api` is an Aspire service name, not a real host. `AddServiceDiscoveryDestinationResolver()`
> plus Aspire's discovery turns it into the actual address/port at runtime. This is why the gateway never
> hardcodes ports.

## JWT at the edge

The gateway validates the token _before_ proxying, so a forged/expired token never reaches a downstream
service. Downstream services still validate (defense in depth) but can trust the gateway has done a first
pass. Use the same signing key, issuer, and audience as the Identity service.

```csharp
// Gateway.Yarp/Extensions/GatewayAuthExtensions.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

public static class GatewayAuthExtensions
{
    public static IServiceCollection AddGatewayJwtAuthentication(
        this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection("Jwt");
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt["Issuer"],
                    ValidateAudience = true,
                    ValidAudience = jwt["Audience"],
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Convert.FromBase64String(jwt["SigningKey"]!)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("authenticated", p => p.RequireAuthenticatedUser())
            .AddPolicy("anonymous", p => p.RequireAssertion(_ => true));
        return services;
    }
}
```

## Rate limiting

Partition per authenticated user (claim `sub`) so a single account can't exhaust the service for
everyone; fall back to remote IP for anonymous traffic. A stricter policy guards auth endpoints against
credential-stuffing.

```csharp
// Gateway.Yarp/Extensions/RateLimiterConfig.cs
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

static void ConfigureRateLimiter(RateLimiterOptions options)
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("per-user", httpContext =>
    {
        var key = httpContext.User.FindFirst("sub")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 100,
            TokensPerPeriod = 100,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    options.AddPolicy("auth-endpoints", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
}
```

## Correlation ID forwarding

The gateway is where a request's correlation ID is born (or honored if the client supplied one). YARP
forwards it downstream so the whole call chain shares one ID in logs and traces. The `UseCorrelationId`
middleware lives in `BuildingBlocks.Web` (see `references/cross-cutting.md`); ensure YARP copies the
header by adding a request transform:

```csharp
// In AddReverseProxy().AddTransforms(...)
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver()
    .AddTransforms(ctx =>
    {
        ctx.AddRequestTransform(transform =>
        {
            var cid = transform.HttpContext.Items["CorrelationId"]?.ToString();
            if (!string.IsNullOrEmpty(cid))
                transform.ProxyRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", cid);
            return ValueTask.CompletedTask;
        });
    });
```

## Swagger aggregation

YARP doesn't aggregate OpenAPI out of the box. The clean approach: proxy each service's `/openapi/v1.json`
through a dedicated route and point a single Swagger UI at all of them via the definitions dropdown.

```csharp
// Gateway.Yarp/Program.cs (additions)
app.UseSwaggerUI(ui =>
{
    ui.SwaggerEndpoint("/openapi/ordering/v1.json", "Ordering API");
    ui.SwaggerEndpoint("/openapi/catalog/v1.json", "Catalog API");
    ui.SwaggerEndpoint("/openapi/identity/v1.json", "Identity API");
    ui.RoutePrefix = "swagger";
});
```

Add passthrough routes so the gateway serves each service's spec:

```jsonc
// Add to ReverseProxy.Routes
"ordering-openapi": {
  "ClusterId": "ordering-cluster",
  "Match": { "Path": "/openapi/ordering/{**catch-all}" },
  "Transforms": [ { "PathPattern": "/openapi/{**catch-all}" } ]
}
```

> For richer merging (one combined document), generate at build time with a tool that reads each service's
> spec, or compose with `Swashbuckle`'s `DocumentFilter`. The per-service dropdown above is the pragmatic
> default and keeps each service's contract authoritative.
