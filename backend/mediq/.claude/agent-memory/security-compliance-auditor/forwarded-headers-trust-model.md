---
name: forwarded-headers-trust-model
description: How the edge XFF/proxy trust model works on .NET 10 and why the default-deny is spoof-proof; the KnownIPNetworks/KnownNetworks synced-collection fact.
metadata:
  type: reference
---

The per-IP rate limiters (gateway 200/min, api 100/min — both `PartitionedRateLimiter` keyed on `ctx.Connection.RemoteIpAddress`) are made XFF-aware via `mediq.ServiceDefaults/ForwardedHeadersConfig.Build(config, logger)`, called as `app.UseForwardedHeaders(...)` BEFORE `app.UseRateLimiter()` in both `mediq.Api/Program.cs` and `mediq.Gateway/Program.cs`. The api per-CLIENT limiter (partitions on clientId in `ApiClientRequestLogMiddleware`/UseApiClientRequestLog, PR#33) is a SEPARATE mechanism — XFF changes do not touch it.

STRICT default-deny trust model (the anti-spoofing crux): `Build` CLEARS `options.KnownIPNetworks` and `options.KnownProxies`, then adds only entries from the `ForwardedHeaders` config section (`KnownProxies` IPs, `KnownNetworks` CIDRs). The committed appsettings ship both arrays EMPTY → 0 networks + 0 proxies → `X-Forwarded-For` is ignored → the limiter partitions on the raw socket IP (no behavior change, no spoofing). A deployment behind a real LB/proxy opts in by populating those arrays. Only `XForwardedFor` is honored (not Proto). Invalid entries are TryParse-skipped + warn-logged (a typo can't crash boot).

KEY .NET 10 FACT (verified from the framework doc XML, `Microsoft.AspNetCore.App.Ref/10.0.3/ref/net10.0/Microsoft.AspNetCore.HttpOverrides.xml`): `ForwardedHeadersOptions` exposes BOTH `KnownIPNetworks` (typed to `System.Net.IPNetwork`, the current API) and `KnownNetworks` (typed to the OBSOLETE `Microsoft.AspNetCore.HttpOverrides.IPNetwork`, marked "Obsolete, please use KnownIPNetworks"). They are TWO VIEWS OF ONE BACKING LIST — the doc states the internal list "keeps ... [both] collections in sync. Modifications through either interface are reflected in the other." Therefore clearing `KnownIPNetworks` ALSO empties `KnownNetworks`, and vice-versa. The "which collection does `UseForwardedHeaders` actually consult" question is MOOT for a default-deny audit: clearing either empties both, so loopback trust cannot leak regardless. (Use this when auditing any future proxy-trust/XFF change — don't re-derive it.)

End-to-end XFF partitioning is NOT integration-testable in this repo: WebApplicationFactory's TestServer synthesizes `RemoteIpAddress=127.0.0.1` and bypasses ForwardedHeaders. The builder's default-deny + parsing is proven by unit tests (`AuthEdgeHardeningUnitTests`); the limiter-still-works-on-socket-IP no-regression is `GatewayRateLimitTests` (in `GatewayTrustBoundaryTests.cs`). This is a faithful fallback, accept it.

Related: the JWT signing-key prod guard shipped in the same wave — see [[prior-audit-decisions]] (Phase-4 auth-edge). The committed dev signing key is still pre-existing in appsettings but is now fail-closed against prod use by `JwtSigningKeyGuard`.
