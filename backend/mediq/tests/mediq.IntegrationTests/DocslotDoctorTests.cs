using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Application.Features.Docslot.Doctors;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Dashboard.Enums;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Create-doctor write path against the live canonical DB. Mirrors the create-booking / register-patient
/// slice: a tenant_owner (holds <c>docslot.doctor.create</c>) POSTs a doctor; the endpoint returns 201 with
/// the new id; tenant_id is taken from the JWT (never a header) and the row is then readable via the
/// tenant-scoped GET /api/v1/doctors. The factory's tenant-scoped cleanup removes the created doctor.
/// </summary>
public sealed class DocslotDoctorTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    [Fact]
    public async Task Create_Doctor_Returns_Created_And_Is_Readable_In_Tenant_List()
    {
        var client = await AuthedClientAsync();

        var fullName = $"Dr ZZTest {Guid.NewGuid():N}"[..24];
        var request = new CreateDoctorRequest(
            FullName: fullName,
            DisplayName: "Dr. ZZ Test",
            DepartmentId: factory.DepartmentId,
            Specialization: "Cardiology",
            Qualifications: ["MBBS", "MD"],
            ConsultationFee: 750.00m,
            Gender: Gender.Female,
            Phone: "+919812345678",
            Email: $"zztest+{Guid.NewGuid():N}@docslot.test",
            ExperienceYears: 12,
            IsAcceptingNewPatients: true);

        // Idempotency-Key is honoured (not required) — supply one, like the other POST write paths.
        var resp = await PostAsync(client, "/api/v1/doctors", Guid.NewGuid().ToString(), request);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<CreateDoctorResult>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.DoctorId);
        Assert.Equal(fullName, created.FullName);
        Assert.Equal(factory.DepartmentId, created.DepartmentId);

        // The new doctor is readable in the tenant-scoped directory (proves tenant_id was set from the JWT).
        var listResp = await client.GetAsync("/api/v1/doctors");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var doctors = await listResp.Content.ReadFromJsonAsync<List<DoctorDto>>();
        Assert.NotNull(doctors);
        var found = doctors!.SingleOrDefault(d => d.DoctorId == created.DoctorId);
        Assert.NotNull(found);
        Assert.Equal(fullName, found!.FullName);
        Assert.Equal("Cardiology", found.Specialization);
        Assert.Equal(750.00m, found.ConsultationFee);
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, DocslotWebAppFactory.AdminPassword, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string idempotencyKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }
}
