using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Triage;
using mediq.SharedDataModel.Docslot.Triage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// AI triage surface: submit a free-text symptom complaint and get an advisory urgency band + red flags +
/// suggested department/doctors from the AI sibling-service LangGraph workflow (a dev stub stands in when the
/// AI service isn't running). The complaint is PHI — it's forwarded to the AI sibling and never logged/cached
/// by this service. Gated by <c>docslot.booking.create</c> (the intake/reception actor that books appointments).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class TriageController(ICommandDispatcher commands, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpPost("triage")]
    [RequirePermission("docslot.booking.create")]
    [ProducesResponseType<TriageResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TriageResultDto>> Triage([FromBody] TriageRequest request, CancellationToken ct)
    {
        // X-Purpose-Of-Use (DPDP) — required by the validator when the triage is bound to a patient/booking,
        // then forwarded to the AI service so its purpose-of-use gate + log fire.
        var purpose = Request.Headers["X-Purpose-Of-Use"].ToString();
        return Ok(await commands.Send(new SubmitTriageCommand(RequireTenant(), request, purpose), ct));
    }

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
