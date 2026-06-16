using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Doctors;
using mediq.Application.Features.Docslot.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>Doctor directory + slot availability. Gated by <c>docslot.doctor.read</c> / <c>docslot.slot.read</c>.</summary>
[ApiController]
[Route("api/v1/doctors")]
[Authorize]
public sealed class DoctorsController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission("docslot.doctor.read")]
    [ProducesResponseType<IReadOnlyList<DoctorDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DoctorDto>>> List(CancellationToken ct)
        => Ok(await queries.Query(new ListDoctorsQuery(RequireTenant()), ct));

    /// <summary>
    /// Provision a doctor into the caller's tenant. Gated by <c>docslot.doctor.create</c> (tenant_owner holds
    /// it). tenant_id comes from the JWT (never a header); an Idempotency-Key header is honoured if present.
    /// </summary>
    [HttpPost]
    [RequirePermission("docslot.doctor.create")]
    [ProducesResponseType<CreateDoctorResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateDoctorResult>> Create([FromBody] CreateDoctorRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateDoctorCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(List), new { doctorId = result.DoctorId }, result);
    }

    [HttpGet("{doctorId:guid}/slots")]
    [RequirePermission("docslot.slot.read")]
    [ProducesResponseType<IReadOnlyList<SlotDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SlotDto>>> Slots(
        Guid doctorId, [FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await queries.Query(new GetDoctorSlotsQuery(RequireTenant(), doctorId, date), ct));

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
