using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Shares ONE <see cref="CommissionPipelineWebAppFactory"/> TestHost across the commission-pipeline test
/// classes (CommissionPipelineTests + DirectDiscountTests). Without this each class spins up its own heavy
/// host + tenant seed; multiple concurrent hosts against the single local Postgres tip the tight connection
/// pool into transient stream-aborts. A collection also runs its classes SEQUENTIALLY, which additionally
/// avoids cross-class interference from the global, cross-tenant <c>settle_earned_attributions</c> sweep.
/// </summary>
[CollectionDefinition("CommissionPipeline")]
public sealed class CommissionPipelineCollection : ICollectionFixture<CommissionPipelineWebAppFactory>;
