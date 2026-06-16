using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Patients;
using mediq.Application.Features.Docslot.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Patient surface. The LIST is masked (PHI). The DETAIL read REQUIRES a declared purpose-of-use (DPDP)
/// and a patient consent on file (else 403). Register is gated by <c>docslot.patient.create</c> (Slice 08
/// added this dedicated key; it is granted to every role that already held <c>docslot.patient.update</c>,
/// so no caller loses access). The old <c>.update</c> key remains valid for actual updates.
/// </summary>
[ApiController]
[Route("api/v1/patients")]
[Authorize]
public sealed class PatientsController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission("docslot.patient.read")]
    [ProducesResponseType<IReadOnlyList<PatientListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PatientListItemDto>>> List(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListPatientsQuery(RequireTenant(), skip, take), ct));

    /// <summary>
    /// Full patient detail. Requires <c>X-Purpose-Of-Use</c> header (DPDP) — logged to
    /// <c>platform.purpose_of_use_log</c> — and an active patient consent. NO clinical PHI is returned.
    /// </summary>
    [HttpGet("{patientId:guid}")]
    [RequirePermission("docslot.patient.read")]
    [ProducesResponseType<PatientDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientDetailDto>> Get(Guid patientId, CancellationToken ct)
    {
        var purpose = Request.Headers["X-Purpose-Of-Use"].ToString();
        if (string.IsNullOrWhiteSpace(purpose))
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A declared purpose-of-use header is required to read a patient record (DPDP)."] });

        var notes = Request.Headers["X-Purpose-Notes"].ToString();
        return Ok(await queries.Query(
            new GetPatientDetailQuery(RequireTenant(), patientId, purpose, string.IsNullOrWhiteSpace(notes) ? null : notes), ct));
    }

    /// <summary>Register a patient (cross-tenant by phone) + tenant link. Gated by docslot.patient.create (see class note).</summary>
    [HttpPost]
    [RequirePermission("docslot.patient.create")]
    [ProducesResponseType<RegisterPatientResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterPatientResult>> Register([FromBody] RegisterPatientRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new RegisterPatientCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(Get), new { patientId = result.PatientId }, result);
    }

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
