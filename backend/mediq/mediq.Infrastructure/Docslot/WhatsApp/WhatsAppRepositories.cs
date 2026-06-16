using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Idempotency gate over <c>docslot.processed_messages</c>. INSERT ... ON CONFLICT DO NOTHING returns the
/// affected-row count: 1 ⇒ first sight (process), 0 ⇒ already processed (skip). The PK on
/// <c>whatsapp_message_id</c> enforces the de-dup at the DB level (safe across scale-out / redelivery).
/// </summary>
public sealed class ProcessedMessageStore(PlatformDbContext db) : IProcessedMessageStore
{
    public async Task<bool> TryMarkProcessedAsync(string whatsappMessageId, DateTime nowUtc, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.processed_messages (whatsapp_message_id, processed_at)
            VALUES (@p0, @p1)
            ON CONFLICT (whatsapp_message_id) DO NOTHING
            """,
            new[]
            {
                new NpgsqlParameter("@p0", whatsappMessageId),
                new NpgsqlParameter("@p1", nowUtc),
            }, ct);
        return affected == 1;
    }
}

/// <summary>Append-only writer for <c>docslot.wa_message_log</c>. <c>content</c> is jsonb → text wrapped as {"text": ...}.</summary>
public sealed class WaMessageLogWriter(PlatformDbContext db) : IWaMessageLogWriter
{
    public async Task LogAsync(WaMessageLogEntry entry, CancellationToken ct)
    {
        var contentJson = entry.ContentText is null
            ? null
            : JsonSerializer.Serialize(new { text = entry.ContentText });

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.wa_message_log
                (log_id, tenant_id, patient_id, conversation_id, whatsapp_message_id,
                 direction, message_type, content, status, sent_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            """,
            new[]
            {
                new NpgsqlParameter("@p0", entry.TenantId),
                Nullable("@p1", entry.PatientId),
                Nullable("@p2", entry.ConversationId),
                Nullable("@p3", entry.WhatsAppMessageId),
                new NpgsqlParameter("@p4", entry.Direction),
                new NpgsqlParameter("@p5", entry.MessageType),
                new NpgsqlParameter("@p6", NpgsqlDbType.Jsonb) { Value = (object?)contentJson ?? DBNull.Value },
                Nullable("@p7", entry.Status),
                new NpgsqlParameter("@p8", entry.SentAtUtc),
            }, ct);
    }

    private static NpgsqlParameter Nullable(string name, object? value) =>
        new(name, value ?? DBNull.Value);
}

/// <summary>
/// Outbox enqueuer (stubbed send). Inserts into <c>docslot.outbox_messages</c> as 'pending'; a separate
/// drain worker (out of scope) would deliver to Meta and flip status. payload is jsonb {to, text, ...}.
/// </summary>
public sealed class OutboxMessageEnqueuer(PlatformDbContext db) : IOutboxMessageEnqueuer
{
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["to"] = message.ToPhone,
            ["text"] = message.Text,
        };
        if (message.Extra is not null)
            foreach (var (k, v) in message.Extra) payload[k] = v;

        var payloadJson = JsonSerializer.Serialize(payload);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.outbox_messages
                (outbox_id, tenant_id, patient_id, message_intent, payload, status,
                 attempt_count, max_attempts, next_retry_at, correlation_id, created_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, 'pending', 0, 5, @p4, @p5, @p4)
            """,
            new[]
            {
                new NpgsqlParameter("@p0", message.TenantId),
                message.PatientId is { } pid ? new NpgsqlParameter("@p1", pid) : new NpgsqlParameter("@p1", DBNull.Value),
                new NpgsqlParameter("@p2", message.MessageIntent),
                new NpgsqlParameter("@p3", NpgsqlDbType.Jsonb) { Value = payloadJson },
                new NpgsqlParameter("@p4", message.NowUtc),
                message.CorrelationId is { } cid ? new NpgsqlParameter("@p5", cid) : new NpgsqlParameter("@p5", DBNull.Value),
            }, ct);
    }
}

/// <summary>Upsertable WhatsApp contact profile (<c>docslot.wa_contact_profiles</c>, unique on tenant+phone).</summary>
public sealed class WaContactProfileRepository(PlatformDbContext db) : IWaContactProfileRepository
{
    public async Task<WaContactProfile?> GetAsync(Guid tenantId, string phone, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ProfileRow>(
                """
                SELECT profile_id AS "ProfileId", tenant_id AS "TenantId", phone AS "Phone",
                       display_name AS "DisplayName", default_booking_for AS "DefaultBookingFor",
                       last_relation AS "LastRelation", linked_patient_id AS "LinkedPatientId",
                       preferred_language AS "PreferredLanguage"
                FROM docslot.wa_contact_profiles
                WHERE tenant_id = @p0 AND phone = @p1
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", phone))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault();
        return r is null
            ? null
            : new WaContactProfile(r.ProfileId, r.TenantId, r.Phone, r.DisplayName, r.DefaultBookingFor,
                r.LastRelation, r.LinkedPatientId, r.PreferredLanguage);
    }

    public async Task UpsertAsync(WaContactProfileUpsert profile, CancellationToken ct)
    {
        // COALESCE(EXCLUDED.x, existing.x): only overwrite a column when a non-null value was supplied,
        // so a turn that doesn't know the display name / relation doesn't blank a previously stored one.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.wa_contact_profiles
                (profile_id, tenant_id, phone, display_name, last_relation, preferred_language,
                 last_seen_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, COALESCE(@p4, 'en'), @p5, @p5, @p5)
            ON CONFLICT (tenant_id, phone) DO UPDATE SET
                display_name = COALESCE(EXCLUDED.display_name, docslot.wa_contact_profiles.display_name),
                last_relation = COALESCE(EXCLUDED.last_relation, docslot.wa_contact_profiles.last_relation),
                preferred_language = COALESCE(@p4, docslot.wa_contact_profiles.preferred_language),
                last_seen_at = EXCLUDED.last_seen_at,
                updated_at = EXCLUDED.updated_at
            """,
            new[]
            {
                new NpgsqlParameter("@p0", profile.TenantId),
                new NpgsqlParameter("@p1", profile.Phone),
                profile.DisplayName is { } dn ? new NpgsqlParameter("@p2", dn) : new NpgsqlParameter("@p2", DBNull.Value),
                profile.LastRelation is { } lr ? new NpgsqlParameter("@p3", lr) : new NpgsqlParameter("@p3", DBNull.Value),
                profile.PreferredLanguage is { } pl ? new NpgsqlParameter("@p4", pl) : new NpgsqlParameter("@p4", DBNull.Value),
                new NpgsqlParameter("@p5", profile.NowUtc),
            }, ct);
    }

    private sealed record ProfileRow(
        Guid ProfileId, Guid TenantId, string Phone, string? DisplayName, string DefaultBookingFor,
        string? LastRelation, Guid? LinkedPatientId, string PreferredLanguage);
}

/// <summary>State-machine persistence over <c>docslot.conversations</c> (one active, non-expired row per tenant+phone).</summary>
public sealed class ConversationRepository(PlatformDbContext db) : IConversationRepository
{
    public async Task<ConversationState?> GetActiveAsync(Guid tenantId, string phone, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ConversationRow>(
                """
                SELECT conversation_id AS "ConversationId", tenant_id AS "TenantId", patient_id AS "PatientId",
                       whatsapp_phone AS "Phone", current_step AS "CurrentStep", context::text AS "ContextJson",
                       detected_language AS "DetectedLanguage", is_active AS "IsActive"
                FROM docslot.conversations
                WHERE tenant_id = @p0 AND whatsapp_phone = @p1 AND is_active = true AND expires_at > NOW()
                ORDER BY last_message_at DESC
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", phone))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault();
        return r is null
            ? null
            : new ConversationState(r.ConversationId, r.TenantId, r.PatientId, r.Phone, r.CurrentStep,
                r.ContextJson ?? "{}", r.DetectedLanguage, r.IsActive);
    }

    public async Task<Guid> CreateAsync(
        Guid tenantId, string phone, string currentStep, string contextJson, string? detectedLanguage,
        DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.conversations
                (conversation_id, tenant_id, whatsapp_phone, current_step, context, detected_language,
                 last_message_at, expires_at, is_active)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p6 + interval '30 minutes', true)
            """,
            new[]
            {
                new NpgsqlParameter("@p0", id),
                new NpgsqlParameter("@p1", tenantId),
                new NpgsqlParameter("@p2", phone),
                new NpgsqlParameter("@p3", currentStep),
                new NpgsqlParameter("@p4", NpgsqlDbType.Jsonb) { Value = contextJson },
                detectedLanguage is { } dl ? new NpgsqlParameter("@p5", dl) : new NpgsqlParameter("@p5", DBNull.Value),
                new NpgsqlParameter("@p6", nowUtc),
            }, ct);
        return id;
    }

    public async Task UpdateAsync(
        Guid conversationId, string currentStep, string contextJson, bool isActive, DateTime nowUtc, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.conversations
            SET current_step = @p1, context = @p2, is_active = @p3,
                last_message_at = @p4, expires_at = @p4 + interval '30 minutes'
            WHERE conversation_id = @p0
            """,
            new[]
            {
                new NpgsqlParameter("@p0", conversationId),
                new NpgsqlParameter("@p1", currentStep),
                new NpgsqlParameter("@p2", NpgsqlDbType.Jsonb) { Value = contextJson },
                new NpgsqlParameter("@p3", isActive),
                new NpgsqlParameter("@p4", nowUtc),
            }, ct);
    }

    private sealed record ConversationRow(
        Guid ConversationId, Guid TenantId, Guid? PatientId, string Phone, string CurrentStep,
        string? ContextJson, string? DetectedLanguage, bool IsActive);
}
