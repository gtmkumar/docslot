using System.Threading.RateLimiting;
using mediq.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OTel, health, service discovery) so YARP can route to discovered services.
builder.AddServiceDefaults();

// --- The gateway is the trust boundary: validate JWTs at the edge (services validate again) ---
var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt["SigningKey"]!)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

// --- Edge rate limiting (defense in depth) ---
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1) }));
});

// --- YARP: routes/clusters from config; sanitize trust-bearing headers on the way downstream ---
// Route table (appsettings.json): six Order=1 anonymous allow-list routes (each pinning Path AND Methods, with
// the YARP-reserved AuthorizationPolicy "anonymous" that disables edge auth) PLUS the Order=100 catch-all
// "platform-api" carrying AuthorizationPolicy "default" (the RequireAuthenticatedUser policy from
// AddAuthorization()). So anything under /api/v1/** that isn't on the allow-list requires a valid JWT at the edge.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
        context.AddRequestTransform(transform =>
        {
            // Correlation id is tracing-ONLY (never authz/tenant), so reminting can't break a flow. We always
            // Remove-then-Add a fresh one: the previous add-if-absent let a client forge an id that flowed into
            // our logs, the echoed response, and the downstream event context. A gateway-minted id is the only
            // trustworthy one.
            const string correlation = "X-Correlation-ID";
            transform.ProxyRequest.Headers.Remove(correlation);
            transform.ProxyRequest.Headers.Add(correlation, Guid.CreateVersion7().ToString());

            // The API derives tenant ONLY from the JWT tenant_id claim (CurrentUserContext has no header
            // fallback; a spoof test confirms it's ignored). Stripping X-Tenant-Id at the edge is defense in
            // depth with zero behavioral risk — a client can never smuggle a tenant via header past the boundary.
            transform.ProxyRequest.Headers.Remove("X-Tenant-Id");
            return ValueTask.CompletedTask;
        }));

var app = builder.Build();

// Fail-fast: refuse to boot with the committed dev JWT key (or a sub-256-bit key) outside Development.
// Safe-by-default — Development allows the dev key (warns); Production bites. Tests opt in via TestHostConfig.
JwtSigningKeyGuard.Validate(app.Configuration, app.Environment, app.Logger);

// X-Forwarded-For aware per-IP rate limiting (STRICT default-deny). MUST run BEFORE UseRateLimiter so the
// limiter partitions on the corrected RemoteIpAddress. Empty "ForwardedHeaders" config → XFF ignored → the
// limiter keeps partitioning on the raw socket IP exactly as before (no behavior change).
app.UseForwardedHeaders(ForwardedHeadersConfig.Build(app.Configuration, app.Logger));

app.UseRateLimiter();

// Minimal security response headers (this is a JSON API edge, NOT a browser origin — HSTS/CSP/X-Frame-Options
// belong to the SPA host, not here). X-Content-Type-Options:nosniff goes on every response; Cache-Control:
// no-store is scoped to the token-bearing auth responses so minted JWTs aren't cached by intermediaries. The
// /ref 302 is deliberately left cacheable.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";

        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api/v1/auth/login")
            || path.StartsWithSegments("/api/v1/auth/refresh")
            || path.StartsWithSegments("/api/v1/oauth/token"))
        {
            headers["Cache-Control"] = "no-store";
        }
        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapReverseProxy();

app.Run();

// Exposed so the gateway integration tests can boot the real edge pipeline via
// WebApplicationFactory<Program>. This is the GLOBAL `Program` the compiler synthesizes from the top-level
// statements above (top-level statements cannot live in a namespace, so this partial must be global too —
// the same pattern mediq.Api uses). mediq.Api ALSO exposes a global `Program`; because the test project
// references both, the gateway reference is pulled in under an `extern alias Gateway` so the two `Program`
// types stay distinguishable (Gateway::Program vs the default global one). See GatewayWebAppFactory.
public partial class Program;
