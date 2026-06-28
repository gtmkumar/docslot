using mediq.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-1 outbox 'processing' reaper against the live canonical DB. If the drain worker dies mid-send a row
/// is left in 'processing' and the claim query (status='pending') never re-picks it → silent message loss.
/// <see cref="IOutboxDrainStore.RequeueStrandedAsync"/> (backed by the SECURITY DEFINER fn
/// <c>docslot.requeue_stranded_outbox</c>) returns rows older than the threshold to 'pending'. We resolve the
/// real store from the host's DI (the same instance the BookingMaintenanceWorker uses) and assert a STALE row
/// is requeued while a FRESH 'processing' row (created just now) is left alone.
/// <para>
/// Rows are seeded/read via the owner connection (no per-tenant scope needed — outbox is not under RLS).
/// </para>
/// </summary>
public sealed class OutboxStrandedReaperTests(WhatsAppOutboxWebAppFactory factory)
    : IClassFixture<WhatsAppOutboxWebAppFactory>
{
    [Fact]
    public async Task Stale_Processing_Row_Is_Requeued_To_Pending_Fresh_One_Is_Not()
    {
        _ = factory.CreateClient();   // boot the host so DI is available

        // A row stuck in 'processing' with created_at older than 5 min (worker died mid-send).
        var stale = await SeedProcessingAsync(createdMinutesAgo: 10);
        // A row that JUST entered 'processing' (a healthy in-flight send) — must NOT be reaped.
        var fresh = await SeedProcessingAsync(createdMinutesAgo: 0);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDrainStore>();
        var requeued = await store.RequeueStrandedAsync(TimeSpan.FromMinutes(5), default);

        Assert.True(requeued >= 1, "the stale processing row should have been requeued");
        Assert.Equal("pending", await StatusAsync(stale));      // reaped back to pending
        Assert.Equal("processing", await StatusAsync(fresh));   // in-flight row untouched

        await DeleteAsync(stale, fresh);
    }

    [Fact]
    public async Task SqlFunction_Requeue_Stranded_Outbox_Returns_Pending_For_Stale_Row()
    {
        // Same invariant proven directly against the SECURITY DEFINER function (mocks never catch a wrong
        // status-string or interval predicate; real PG does).
        var stale = await SeedProcessingAsync(createdMinutesAgo: 10);

        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand("SELECT docslot.requeue_stranded_outbox('5 minutes')", conn);
        var n = (int)(await cmd.ExecuteScalarAsync())!;
        Assert.True(n >= 1);
        Assert.Equal("pending", await StatusAsync(stale));

        await DeleteAsync(stale);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private async Task<Guid> SeedProcessingAsync(int createdMinutesAgo)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.outbox_messages
                (outbox_id, tenant_id, message_intent, payload, status, attempt_count, max_attempts, created_at)
            VALUES (@id, @tid, 'test_message', @payload::jsonb, 'processing', 0, 5, NOW() - make_interval(mins => @ago))
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", factory.TenantId);
        cmd.Parameters.AddWithValue("payload", System.Text.Json.JsonSerializer.Serialize(new { to = "919000000099", text = "stranded" }));
        cmd.Parameters.AddWithValue("ago", createdMinutesAgo);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<string> StatusAsync(Guid outboxId)
    {
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM docslot.outbox_messages WHERE outbox_id=@id", conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task DeleteAsync(params Guid[] ids)
    {
        await using var conn = await OpenOwnerAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM docslot.outbox_messages WHERE outbox_id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", ids);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var conn = new NpgsqlConnection(WhatsAppOutboxWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
