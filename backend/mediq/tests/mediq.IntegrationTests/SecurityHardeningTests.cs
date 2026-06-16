using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Application.Features.Security;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-05 security-substrate invariants against the live canonical DB: field encrypt/decrypt round-trip
/// + key_usage logged; audit append-only enforced at the DB; cryptographic erasure produces a certificate
/// and makes ciphertext unrecoverable; portability export assembles; break-glass logs is_break_glass.
/// </summary>
public sealed class SecurityHardeningTests(SecurityWebAppFactory factory) : IClassFixture<SecurityWebAppFactory>
{
    [Fact]
    public async Task Field_Encryption_RoundTrips_And_Logs_Key_Usage()
    {
        using var scope = factory.Services.CreateScope();
        var enc = scope.ServiceProvider.GetRequiredService<IFieldEncryptionService>();

        // mfa_secret is a registered encrypted field (platform.users.mfa_secret → mfa_secrets data class).
        var field = new FieldRef("platform", "users", "mfa_secret");
        Assert.True(await enc.IsRegisteredAsync(field, default));

        const string plaintext = "JBSWY3DPEHPK3PXP";   // a TOTP secret
        var ctx = new EncryptionContext(factory.AdminUserId, factory.TenantId, "user_mfa", factory.AdminUserId, "127.0.0.1");

        var envelope = await enc.EncryptAsync(field, factory.TenantId, plaintext, ctx, default);
        Assert.NotEqual(plaintext, envelope);                 // ciphertext, not plaintext
        Assert.DoesNotContain(plaintext, envelope);

        var decrypted = await enc.DecryptAsync(field, envelope, ctx, default);
        Assert.Equal(plaintext, decrypted);                   // round-trips

        // key_usage_log recorded an encrypt + a decrypt for this tenant's key.
        var usageCount = await ScalarIntAsync(
            "SELECT COUNT(*)::int FROM platform.key_usage_log u JOIN platform.encryption_keys k ON k.key_id=u.key_id WHERE k.tenant_id=@t",
            ("t", factory.TenantId));
        Assert.True(usageCount >= 2, $"expected >=2 key_usage rows, got {usageCount}");
    }

    [Fact]
    public async Task Audit_Log_Is_Append_Only_At_The_Database()
    {
        await using var conn = new NpgsqlConnection(SecurityWebAppFactory.ConnectionString);
        await conn.OpenAsync();

        // Run inside a transaction that ROLLS BACK, so the probe insert (and its hash-chain row) leave no
        // residue and the live chain is never corrupted — deleting a chained audit row would break it (which
        // is precisely the tamper-evidence this guard protects). The guard trigger still fires on the
        // UPDATE/DELETE attempts within the transaction.
        await using var tx = await conn.BeginTransactionAsync();
        var auditId = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO platform.audit_log (audit_id, action, resource_type, success) VALUES (@id,'test','test',true)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", auditId);
            await ins.ExecuteNonQueryAsync();
        }

        // UPDATE blocked by the substrate guard trigger (regardless of role).
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var upd = new NpgsqlCommand("UPDATE platform.audit_log SET action='x' WHERE audit_id=@id", conn, tx);
            upd.Parameters.AddWithValue("id", auditId);
            await upd.ExecuteNonQueryAsync();
        });
        // The failed statement aborts the tx; restart for the DELETE assertion.
        await tx.RollbackAsync();

        await using var tx2 = await conn.BeginTransactionAsync();
        await using (var ins2 = new NpgsqlCommand(
            "INSERT INTO platform.audit_log (audit_id, action, resource_type, success) VALUES (@id,'test','test',true)", conn, tx2))
        {
            ins2.Parameters.AddWithValue("id", auditId);
            await ins2.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var del = new NpgsqlCommand("DELETE FROM platform.audit_log WHERE audit_id=@id", conn, tx2);
            del.Parameters.AddWithValue("id", auditId);
            await del.ExecuteNonQueryAsync();
        });
        await tx2.RollbackAsync();   // probe leaves NO residue; live chain stays intact
    }

    [Fact]
    public async Task Cryptographic_Erasure_Produces_Certificate_And_Renders_Data_Unrecoverable()
    {
        // Seed a deletion request (FK for the certificate).
        var deletionRequestId = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO platform.data_deletion_requests (request_id, requester_type, subject_phone, status, created_at)
            VALUES (@id, 'patient', @phone, 'verified', NOW())
            """,
            ("id", deletionRequestId), ("phone", factory.PatientPhone));

        using var scope = factory.Services.CreateScope();
        var enc = scope.ServiceProvider.GetRequiredService<IFieldEncryptionService>();
        var erasure = scope.ServiceProvider.GetRequiredService<ICryptoErasureService>();

        // Encrypt a medical_history field for the subject's tenant FIRST, so there is a key to destroy.
        var field = new FieldRef("docslot", "patient_medical_history", "description");
        var ctx = new EncryptionContext(factory.AdminUserId, factory.TenantId, "medical_history", factory.PatientId, "127.0.0.1");
        var envelope = await enc.EncryptAsync(field, factory.TenantId, "patient has penicillin allergy", ctx, default);
        Assert.Equal("patient has penicillin allergy", await enc.DecryptAsync(field, envelope, ctx, default));   // recoverable before erasure

        // Erase: destroys the subject's keys, records a certificate.
        var result = await erasure.EraseAsync(deletionRequestId, factory.PatientPhone, factory.AdminUserId, default);
        Assert.NotEqual(Guid.Empty, result.CertificateId);
        Assert.NotEmpty(result.DestroyedKeyIds);
        Assert.NotEqual(result.PreHash, result.PostHash);     // state changed

        // Certificate row exists.
        var certCount = await ScalarIntAsync(
            "SELECT COUNT(*)::int FROM platform.deletion_certificates WHERE deletion_request_id=@r", ("r", deletionRequestId));
        Assert.Equal(1, certCount);

        // The ciphertext is now UNRECOVERABLE (the key was cryptographically destroyed).
        await Assert.ThrowsAnyAsync<Exception>(async () => await enc.DecryptAsync(field, envelope, ctx, default));
    }

    [Fact]
    public async Task Portability_Export_Assembles_A_Bundle()
    {
        var client = await AuthedClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/security/dpdp/export", new { subjectPhone = factory.PatientPhone });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<DataExportResult>();
        Assert.NotNull(result);
        Assert.Equal("fhir_r4_bundle", result!.Format);
        Assert.Contains("\"resourceType\":\"Bundle\"", result.BundleJson);
        Assert.False(string.IsNullOrWhiteSpace(result.Checksum));
    }

    [Fact]
    public async Task Audit_Chain_Verifies_Intact()
    {
        var client = await AuthedClientAsync();
        var resp = await client.GetAsync("/api/v1/security/audit-chain/verify");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<AuditChainVerifyResult>();
        Assert.NotNull(result);
        Assert.True(result!.Intact, "audit chain should be intact (no broken links)");
    }

    [Fact]
    public async Task BreakGlass_Logs_Is_Break_Glass_And_Flags_Review()
    {
        var client = await AuthedClientAsync();
        var resourceId = factory.PatientId;
        var resp = await client.PostAsJsonAsync("/api/v1/security/break-glass", new
        {
            resourceType = "patient",
            resourceId,
            justification = "Emergency: unconscious patient, need allergy history immediately."
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // A purpose_of_use_log row with is_break_glass=true and review_required=true was written.
        var count = await ScalarIntAsync(
            "SELECT COUNT(*)::int FROM platform.purpose_of_use_log WHERE accessed_resource_id=@r AND is_break_glass=true AND review_required=true",
            ("r", resourceId));
        Assert.True(count >= 1);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, SecurityWebAppFactory.AdminPassword, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<int> ScalarIntAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(SecurityWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(SecurityWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
