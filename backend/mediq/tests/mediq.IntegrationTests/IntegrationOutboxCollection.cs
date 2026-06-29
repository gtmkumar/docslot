using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Shares ONE <see cref="IntegrationOutboxWebAppFactory"/> TestHost across the integration-outbox test classes
/// (IntegrationOutboxTests + RetentionPruneTests) and — via the collection — runs them SEQUENTIALLY. Both drive
/// the SHARED HOT <c>platform_api.integration_event_outbox</c> / <c>webhook_deliveries</c> tables: the retention
/// pruner issues GLOBAL DELETEs of aged success rows, so serializing the two classes (and sharing one host +
/// tenant seed) avoids cross-class interference and keeps the single local Postgres connection pool from being
/// tipped by multiple concurrent heavy hosts.
/// </summary>
[CollectionDefinition("IntegrationOutbox")]
public sealed class IntegrationOutboxCollection : ICollectionFixture<IntegrationOutboxWebAppFactory>;
