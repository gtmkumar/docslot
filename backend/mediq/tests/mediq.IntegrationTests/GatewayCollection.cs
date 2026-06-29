using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Groups the gateway edge tests into ONE collection so they run SEQUENTIALLY relative to each other rather
/// than both spinning their WebApplicationFactory hosts concurrently. The gateway tests touch no Postgres
/// directly, but each host + the rate-limit test's 200-request burst adds CPU/thread pressure that, run in
/// parallel with the timing-sensitive DB-concurrency tests (audit-chain-under-parallel-writes, tenant-scope
/// no-bleed), was enough to tip those into transient flakes on the shared local Postgres. Serializing the
/// gateway classes against each other (same rationale as <see cref="CommissionPipelineCollection"/>) removes
/// that overlap. No shared fixture: <c>GatewayRateLimitTests</c> deliberately owns its own factory so its
/// 127.0.0.1 limiter bucket can't poison <c>GatewayTrustBoundaryTests</c>.
/// </summary>
[CollectionDefinition("Gateway")]
public sealed class GatewayCollection;
