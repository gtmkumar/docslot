using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Ai;
using mediq.Application.Features.Docslot.Clinical;
using mediq.SharedDataModel.Docslot.Ai;
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

    // Amend an ISSUED prescription: mints a new prescription that supersedes the original (which is marked
    // 'amended'). Distinct 'amend' permission (separation from issue); the original is never overwritten.
    [HttpPost("prescriptions/{prescriptionId:guid}/amend")]
    [RequirePermission("docslot.prescription.amend")]
    [ProducesResponseType<AmendPrescriptionResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AmendPrescriptionResult>> AmendPrescription(
        Guid prescriptionId, [FromBody] AmendPrescriptionRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new AmendPrescriptionCommand(RequireTenant(), prescriptionId, request), ct);
        return CreatedAtAction(nameof(GetPrescription), new { prescriptionId = result.PrescriptionId }, result);
    }

    [HttpGet("patients/{patientId:guid}/prescriptions")]
    [RequirePermission("docslot.prescription.read")]
    [ProducesResponseType<IReadOnlyList<PrescriptionListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PrescriptionListItemDto>>> ListPrescriptions(Guid patientId, CancellationToken ct)
        => Ok(await queries.Query(new ListPrescriptionsQuery(RequireTenant(), patientId), ct));

    /// <summary>A prescription's drug-safety alerts (allergy/interaction/duplicate), generated at issue/amend.
    /// PHI — consent + X-Purpose-Of-Use gated (break-glass aware), same gate as reading the prescription.</summary>
    [HttpGet("prescriptions/{prescriptionId:guid}/drug-alerts")]
    [RequirePermission("docslot.prescription.read")]
    [ProducesResponseType<IReadOnlyList<DrugAlertDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DrugAlertDto>>> GetDrugAlerts(Guid prescriptionId, CancellationToken ct)
        => Ok(await queries.Query(new GetPrescriptionDrugAlertsQuery(RequireTenant(), prescriptionId, Purpose()), ct));

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

    // Attach the PHI artifact (PDF/image) — envelope-encrypted at rest in tenant-scoped blob storage.
    // RequestSizeLimit bounds the body at the pipeline (before model binding buffers it) — defense-in-depth
    // with the validator's base64-length cap against an unbounded-upload DoS (auditor finding).
    [HttpPost("lab-reports/{reportId:guid}/file")]
    [RequirePermission("docslot.report.upload")]
    [RequestSizeLimit(30_000_000)]
    [ProducesResponseType<SetLabReportFileResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SetLabReportFileResult>> SetReportFile(
        Guid reportId, [FromBody] SetLabReportFileRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new SetLabReportFileCommand(RequireTenant(), reportId, request), ct);
        return CreatedAtAction(nameof(GetReport), new { reportId = result.ReportId }, result);
    }

    // Download the PHI artifact — consent + purpose-of-use + break-glass gated (same gate as reading results).
    [HttpGet("lab-reports/{reportId:guid}/file")]
    [RequirePermission("docslot.report.read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadReportFile(Guid reportId, CancellationToken ct)
    {
        var file = await queries.Query(new GetLabReportFileQuery(RequireTenant(), reportId, Purpose()), ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

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

    /// <summary>Add a medical-history record (PHI — title/description encrypted at rest). Distinct create permission.</summary>
    [HttpPost("patients/{patientId:guid}/medical-history")]
    [RequirePermission("docslot.medical_history.create")]
    [ProducesResponseType<CreateMedicalHistoryResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateMedicalHistoryResult>> CreateMedicalHistory(
        Guid patientId, [FromBody] CreateMedicalHistoryRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateMedicalHistoryCommand(RequireTenant(), patientId, request), ct);
        return CreatedAtAction(nameof(MedicalHistory), new { patientId }, result);
    }

    /// <summary>Update a medical-history record in place (re-encrypts PHI). 404 if it isn't in the caller's tenant.</summary>
    [HttpPut("patients/{patientId:guid}/medical-history/{historyId:guid}")]
    [RequirePermission("docslot.medical_history.update")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> UpdateMedicalHistory(
        Guid patientId, Guid historyId, [FromBody] UpdateMedicalHistoryRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateMedicalHistoryCommand(RequireTenant(), historyId, request), ct));

    // ---- ABDM (consent-required) -----------------------------------------------------------------

    [HttpPost("abdm/records")]
    [RequirePermission("docslot.abdm.records.create")]
    [ProducesResponseType<PushAbdmRecordResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<PushAbdmRecordResult>> PushAbdm([FromBody] PushAbdmRecordRequest request, CancellationToken ct)
        => Ok(await commands.Send(new PushAbdmRecordCommand(RequireTenant(), request), ct));

    /// <summary>Publish a stored ABDM record's care context to the national network (HIP data push, via the ABDM
    /// gateway). Active ABDM consent required (else 403); idempotent (re-link returns the existing care context).
    /// Gated by the DANGEROUS <c>docslot.abdm.records.link</c> — national-network PHI egress is a distinct, higher
    /// privilege than local store (<c>.create</c>), so it is excluded from the tenant_admin auto-grant.</summary>
    [HttpPost("abdm/records/{recordId:guid}/link")]
    [RequirePermission("docslot.abdm.records.link")]
    [ProducesResponseType<LinkAbdmRecordResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LinkAbdmRecordResult>> LinkAbdm(Guid recordId, CancellationToken ct)
        => Ok(await commands.Send(new LinkAbdmRecordCommand(RequireTenant(), recordId), ct));

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

    // ---- AI document surfaces (proxied to the Python AI sibling; slice 11) -----------------------

    /// <summary>Extract structured analytes from a lab-report image via the AI sibling service. The result is
    /// lab PHI → gated by <c>docslot.report.read</c> + consent (break-glass aware) + X-Purpose-Of-Use (forwarded
    /// to the AI service, which persists the extraction + writes the purpose log). Never cached. <c>available=false</c>
    /// only when the AI service is unreachable; an authorization/validation denial surfaces as 403/422.</summary>
    [HttpPost("lab-reports/extract")]
    [RequirePermission("docslot.report.read")]
    [ProducesResponseType<OcrExtractionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OcrExtractionDto>> ExtractLabReport([FromBody] ExtractLabReportRequest request, CancellationToken ct)
        => Ok(await commands.Send(new ExtractLabReportCommand(RequireTenant(), request, RawPurpose()), ct));

    /// <summary>Ask a natural-language question over a patient's indexed medical history (RAG, read-only — never
    /// indexes). The answer is PHI → gated by <c>docslot.medical_history.read</c> + consent (break-glass aware) +
    /// X-Purpose-Of-Use (forwarded). Never cached. The question is a request-body value, never logged/cached.</summary>
    [HttpPost("patients/{patientId:guid}/rag/ask")]
    [RequirePermission("docslot.medical_history.read")]
    [ProducesResponseType<RagAnswerDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RagAnswerDto>> AskRag(Guid patientId, [FromBody] RagAskRequest request, CancellationToken ct)
        => Ok(await commands.Send(new AskRagCommand(RequireTenant(), patientId, request, RawPurpose()), ct));

    /// <summary>List the tenant's recent OCR extraction SUMMARIES (ops/forensics — no analyte PHI). Tenant-scoped
    /// by the AI service via the forwarded JWT; gated by <c>docslot.report.read</c>. available=false when the AI
    /// service is unreachable.</summary>
    [HttpGet("ai/extractions")]
    [RequirePermission("docslot.report.read")]
    [ProducesResponseType<OcrExtractionListDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OcrExtractionListDto>> ListExtractions([FromQuery] int limit, CancellationToken ct)
        => Ok(await queries.Query(new ListAiExtractionsQuery(limit <= 0 ? 20 : limit), ct));

    /// <summary>The tenant's RAG knowledge-base STATUS (operational counts — embeddings/patients-indexed/KBs; no
    /// PHI). Tenant-scoped by the AI service via the forwarded JWT; gated by <c>docslot.medical_history.read</c>.</summary>
    [HttpGet("ai/rag/status")]
    [RequirePermission("docslot.medical_history.read")]
    [ProducesResponseType<RagStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RagStatusDto>> RagStatus(CancellationToken ct)
        => Ok(await queries.Query(new GetRagStatusQuery(), ct));

    // ---- helpers ---------------------------------------------------------------------------------

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

    /// <summary>The raw X-Purpose-Of-Use header (may be empty). The AI-proxy command VALIDATORS require it (422
    /// if missing) so the .NET gate mirrors the AI-side purpose-of-use gate; it is then forwarded to the AI service.</summary>
    private string RawPurpose() => Request.Headers["X-Purpose-Of-Use"].ToString();

    private string Purpose()
    {
        var purpose = Request.Headers["X-Purpose-Of-Use"].ToString();
        if (string.IsNullOrWhiteSpace(purpose))
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A declared purpose-of-use header is required to read clinical PHI (DPDP)."] });
        return purpose;
    }
}
