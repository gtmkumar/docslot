using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Clinical;
using mediq.SharedDataModel.Docslot.Clinical;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Clinical PHI surface (slice 03b). Every field is encrypted at rest; every read requires
/// <c>X-Purpose-Of-Use</c> (DPDP, logged) + an active patient consent; ABDM is additionally gated by an
/// active ABDM consent; RLS confines rows to the JWT tenant. Gated by the real <c>docslot.*</c> seed keys.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class ClinicalController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    // ---- Prescriptions ---------------------------------------------------------------------------

    [HttpPost("prescriptions")]
    [RequirePermission("docslot.prescription.create")]
    [ProducesResponseType<IssuePrescriptionResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<IssuePrescriptionResult>> Issue([FromBody] IssuePrescriptionRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new IssuePrescriptionCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(GetPrescription), new { prescriptionId = result.PrescriptionId }, result);
    }

    [HttpGet("prescriptions/{prescriptionId:guid}")]
    [RequirePermission("docslot.prescription.read")]
    [ProducesResponseType<PrescriptionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PrescriptionDto>> GetPrescription(Guid prescriptionId, CancellationToken ct)
        => Ok(await queries.Query(new GetPrescriptionQuery(RequireTenant(), prescriptionId, Purpose()), ct));

    [HttpGet("patients/{patientId:guid}/prescriptions")]
    [RequirePermission("docslot.prescription.read")]
    [ProducesResponseType<IReadOnlyList<PrescriptionListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PrescriptionListItemDto>>> ListPrescriptions(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new ListPrescriptionsQuery(RequireTenant(), patientId), ct));

    // ---- Lab reports -----------------------------------------------------------------------------

    [HttpPost("lab-reports")]
    [RequirePermission("docslot.report.upload")]
    [ProducesResponseType<UploadLabReportResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UploadLabReportResult>> UploadReport([FromBody] UploadLabReportRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new UploadLabReportCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(GetReport), new { reportId = result.ReportId }, result);
    }

    [HttpGet("lab-reports/{reportId:guid}")]
    [RequirePermission("docslot.report.read")]
    [ProducesResponseType<LabReportDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LabReportDto>> GetReport(Guid reportId, CancellationToken ct)
        => Ok(await queries.Query(new GetLabReportQuery(RequireTenant(), reportId, Purpose()), ct));

    /// <summary>List a patient's lab reports — headers only (no decrypted body). Requires X-Purpose-Of-Use + consent.</summary>
    [HttpGet("patients/{patientId:guid}/lab-reports")]
    [RequirePermission("docslot.report.read")]
    [ProducesResponseType<IReadOnlyList<LabReportListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LabReportListItemDto>>> ListReports(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new ListLabReportsQuery(RequireTenant(), patientId, Purpose()), ct));

    /// <summary>Mark a lab report delivered (status→delivered, emits docslot.report.delivered). Gated by docslot.report.deliver.</summary>
    [HttpPost("patients/{patientId:guid}/lab-reports/{reportId:guid}/deliver")]
    [RequirePermission("docslot.report.deliver")]
    [ProducesResponseType<DeliverLabReportResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliverLabReportResult>> DeliverReport(Guid patientId, Guid reportId, CancellationToken ct)
        => Ok(await commands.Send(new DeliverLabReportCommand(RequireTenant(), reportId), ct));

    /// <summary>A patient's clinical-access context (clinical + ABDM consent state). No PHI beyond a masked phone.</summary>
    [HttpGet("patients/{patientId:guid}/consent")]
    [RequirePermission("docslot.patient.read")]
    [ProducesResponseType<PatientConsentDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PatientConsentDto>> Consent(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new GetPatientConsentQuery(RequireTenant(), patientId), ct));

    // ---- Medical history -------------------------------------------------------------------------

    [HttpGet("patients/{patientId:guid}/medical-history")]
    [RequirePermission("docslot.medical_history.read")]
    [ProducesResponseType<IReadOnlyList<MedicalHistoryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MedicalHistoryDto>>> MedicalHistory(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new ListMedicalHistoryQuery(RequireTenant(), patientId, Purpose()), ct));

    // ---- ABDM (consent-required) -----------------------------------------------------------------

    [HttpPost("abdm/records")]
    [RequirePermission("docslot.abdm.records.create")]
    [ProducesResponseType<PushAbdmRecordResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PushAbdmRecordResult>> PushAbdm([FromBody] PushAbdmRecordRequest request, CancellationToken ct)
        => Ok(await commands.Send(new PushAbdmRecordCommand(RequireTenant(), request), ct));

    [HttpGet("abdm/records/{recordId:guid}")]
    [RequirePermission("docslot.abdm.records.read")]
    [ProducesResponseType<AbdmRecordDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AbdmRecordDto>> GetAbdm(Guid recordId, CancellationToken ct)
        => Ok(await queries.Query(new GetAbdmRecordQuery(RequireTenant(), recordId, Purpose()), ct));

    /// <summary>List a patient's ABDM records — headers only (no decrypted FHIR). Consent-REQUIRED + X-Purpose-Of-Use.</summary>
    [HttpGet("patients/{patientId:guid}/abdm-records")]
    [RequirePermission("docslot.abdm.records.read")]
    [ProducesResponseType<IReadOnlyList<AbdmRecordListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AbdmRecordListItemDto>>> ListAbdm(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new ListAbdmRecordsQuery(RequireTenant(), patientId, Purpose()), ct));

    // ---- helpers ---------------------------------------------------------------------------------

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

    private string Purpose()
    {
        var purpose = Request.Headers["X-Purpose-Of-Use"].ToString();
        if (string.IsNullOrWhiteSpace(purpose))
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A declared purpose-of-use header is required to read clinical PHI (DPDP)."] });
        return purpose;
    }
}
