using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace mediq.ServiceDefaults;

/// <summary>
/// Fail-fast PRODUCTION guard for the JWT signing key. Refuses to boot a host that is configured with the
/// committed development signing key, or with a sub-256-bit key, outside the Development environment.
/// <para>
/// This closes the "the dev key silently reaches production" hole: the key is base64 in appsettings and is the
/// same symmetric secret the gateway and API both validate with, so leaking it into a prod deployment would let
/// anyone mint accepted tokens. The guard fails CLOSED — on any doubt it throws, preventing the host from
/// serving.
/// </para>
/// <para>
/// Safe-by-default: a normal local <c>dotnet run</c> runs in Development, so <c>allowDev</c> defaults to true →
/// the dev key warns and boots exactly as before. Production (the default when <c>ASPNETCORE_ENVIRONMENT</c> is
/// unset) leaves <c>allowDev</c> false → the guard bites. The whole integration-test suite is centrally exempted
/// by setting <c>Jwt__AllowDevSigningKey=true</c> in the test [ModuleInitializer]; the dedicated prod-guard test
/// flips it back to false (+ Production) to assert the throw.
/// </para>
/// The real RS256/JWKS migration + secret store stays DEFERRED (external-credential-gated); this guard is the
/// interim enforcement that the committed symmetric dev key can never be served in production.
/// </summary>
public static class JwtSigningKeyGuard
{
    /// <summary>
    /// The exact committed development signing key (base64). Equality against this value — not the length check —
    /// is what catches the dev key, because it decodes to 51 bytes (>= the 32-byte / 256-bit minimum) and would
    /// otherwise pass the length gate.
    /// </summary>
    public const string DevSigningKeyBase64 = "ZGV2LW9ubHktc2lnbmluZy1rZXktcmVwbGFjZS1pbi1wcm9kdWN0aW9uLTI1Ni1iaXQh";

    /// <summary>
    /// Validates <c>Jwt:SigningKey</c> and throws <see cref="InvalidOperationException"/> if it is unsafe for the
    /// current environment. Call this immediately after <c>builder.Build()</c> so a throw surfaces during the
    /// host's start (and during a test factory's <c>CreateClient()</c>), preventing it from ever serving.
    /// </summary>
    /// <param name="config">App configuration (reads <c>Jwt:SigningKey</c> and <c>Jwt:AllowDevSigningKey</c>).</param>
    /// <param name="env">Host environment; <c>allowDev</c> defaults to <see cref="HostEnvironmentEnvExtensions.IsDevelopment"/>.</param>
    /// <param name="logger">Optional; a warning is logged when the dev key is accepted (Development).</param>
    public static void Validate(IConfiguration config, IHostEnvironment env, ILogger? logger = null)
    {
        var key = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Jwt:SigningKey is not configured.");

        int len;
        try { len = Convert.FromBase64String(key).Length; }
        catch (FormatException) { throw new InvalidOperationException("Jwt:SigningKey is not valid base64."); }

        // Default: the dev key is allowed ONLY in Development. A deployment/test can override Jwt:AllowDevSigningKey
        // (the test suite sets it true centrally; Production leaves it false so the guard bites).
        var allowDev = config.GetValue("Jwt:AllowDevSigningKey", env.IsDevelopment());
        var isDevKey = string.Equals(key, DevSigningKeyBase64, StringComparison.Ordinal);

        if (isDevKey && !allowDev)
            throw new InvalidOperationException(
                "The committed development JWT signing key is configured outside Development — refusing to start. " +
                "Supply a production key via Jwt__SigningKey (env var / Aspire parameter / secret store).");

        if (isDevKey)
            logger?.LogWarning(
                "JWT signing key is the COMMITTED DEV KEY — acceptable only in development. " +
                "Set Jwt__SigningKey from a secret store before production.");

        if (len < 32 && !allowDev)
            throw new InvalidOperationException(
                $"Jwt:SigningKey decodes to {len} bytes (< 256-bit minimum) — refusing to start outside development.");
    }
}
