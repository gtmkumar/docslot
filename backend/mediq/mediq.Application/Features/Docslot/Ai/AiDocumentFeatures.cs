using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Ai;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Ai;

// ===================================================================================================
// Slice-11 — surface the AI sibling service's OCR (lab-report extraction) + RAG (ask) through the .NET
// API, mirroring the no-show (PR #34) / triage (PR #35) honest-stub seam.
//
// Both are ISelfManagedTransaction: the handler does an EXTERNAL HTTP call (the AI service) so it must NOT
// hold a pooled DB connection across the network hop — it opens its OWN tenant scope JUST to authorize
// (consent + break-glass + tenant link, all RLS-scoped), releases it, THEN calls the AI service outside any
// tx (the no-show/payout/ABDM gateway-outside-tx pattern).
//
// Both are IDoNotCacheResponse: the result is decrypted PHI (lab analytes / a synthesized clinical answer)
// → never persist it to the plaintext idempotency store.
//
// PURPOSE-OF-USE is single-sourced on the AI side: the AI service runs its own consent gate and writes
// platform.purpose_of_use_log; the .NET proxy is NOT the terminal reader, so it only FORWARDS
// X-Purpose-Of-Use (and pre-checks consent so an AI 4xx is never masked as "unavailable"). It deliberately
// does not write a .NET-side purpose row (avoids duplicate/divergent ledger entries).
// ===================================================================================================

// ---- OCR: extract structured analytes from a lab-report image -------------------------------------

public sealed record ExtractLabReportCommand(Guid TenantId, ExtractLabReportRequest Request, string? DeclaredPurpose)
    : ICommand<OcrExtractionDto>, ISelfManagedTransaction, IDoNotCacheResponse;

public sealed class ExtractLabReportValidator : AbstractValidator<ExtractLabReportCommand>
{
    public ExtractLabReportValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.RelatedPatientId).NotEmpty()
            .WithMessage("relatedPatientId is required — a lab-report extraction is a patient-bound PHI access.");
        // DPDP: the extraction is ALWAYS patient-bound → X-Purpose-Of-Use is REQUIRED (mirrors the AI-side gate so
        // the AI's 400 is never masked as "unavailable"; the AI service writes the purpose-of-use log).
        RuleFor(x => x.DeclaredPurpose).NotEmpty()
            .WithMessage("A declared purpose-of-use (X-Purpose-Of-Use header) is required to extract a lab report (DPDP).");
    }
}

public sealed class ExtractLabReportCommandHandler(
    IAiOcrClient ocr, IPatientRepository patients, IBreakGlassService breakGlass, IUnitOfWork uow, ICurrentUserContext ctx)
    : ICommandHandler<ExtractLabReportCommand, OcrExtractionDto>
{
    public async Task<OcrExtractionDto> Handle(ExtractLabReportCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var req = command.Request;

        // ── Phase 1 — authorize in an OWN committed tenant scope (the break-glass read is RLS-scoped), then
        //    release the pooled connection BEFORE the AI hop (no connection held across the network I/O).
        await using (var scope = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            // Tenant isolation (defense-in-depth; the AI service also enforces this against the JWT tenant): the
            // patient must be linked to the caller's tenant — else a tenant-A caller could reach a globally-consented
            // patient that is only linked to tenant B. Surfaces as 404 (resource not found in this tenant).
            if (!await patients.IsLinkedToTenantAsync(req.RelatedPatientId, command.TenantId, ct))
                throw new KeyNotFoundException("Patient not found in this tenant.");

            // Consent gate (+ break-glass override): the OCR result is lab PHI → the SAME gate as reading a lab
            // report. A patient-wide lab_report grant unlocks (no specific report id exists pre-extraction).
            var patient = await patients.GetByIdAsync(req.RelatedPatientId, ct);
            if (patient is null || !patient.HasActiveConsent)
            {
                var grant = await breakGlass.GetActiveGrantAsync(userId, command.TenantId, req.RelatedPatientId, "lab_report", null, ct);
                if (grant is null)
                    throw new ForbiddenException("Patient has no active consent; lab-report extraction refused (DPDP).");
            }
            await scope.CommitAsync(ct);
        }

        // ── Phase 2 — call the AI service OUTSIDE any DB tx. The AI persists the extraction + writes the
        //    purpose-of-use log; the adapter forwards the caller JWT + the declared purpose.
        var result = await ocr.ExtractLabReportAsync(
            new OcrExtractInput(req.RelatedPatientId, req.RelatedBookingId, command.DeclaredPurpose!), ct);

        if (result is null)
            return new OcrExtractionDto(Available: false, null, null, null, null, null, []);

        return new OcrExtractionDto(
            Available: true, result.ExtractionId, result.OcrEngine, result.OverallConfidence,
            result.RequiresHumanReview, result.AbnormalCount,
            result.Analytes.Select(a => new AnalyteDto(a.Test, a.Value, a.Unit, a.RefLow, a.RefHigh, a.Flag)).ToList(),
            result.Source);
    }
}

// ---- RAG: ask a question over a patient's indexed medical history (read-only) ----------------------

public sealed record AskRagCommand(Guid TenantId, Guid PatientId, RagAskRequest Request, string? DeclaredPurpose)
    : ICommand<RagAnswerDto>, ISelfManagedTransaction, IDoNotCacheResponse;

public sealed class AskRagValidator : AbstractValidator<AskRagCommand>
{
    public AskRagValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Request.Question).NotEmpty().MaximumLength(4000)   // bound the PHI payload
            .WithMessage("A question (1–4000 chars) is required.");
        // DPDP: a RAG ask is ALWAYS patient-bound → X-Purpose-Of-Use is REQUIRED (mirrors the AI-side gate).
        RuleFor(x => x.DeclaredPurpose).NotEmpty()
            .WithMessage("A declared purpose-of-use (X-Purpose-Of-Use header) is required to query a patient's history (DPDP).");
    }
}

public sealed class AskRagCommandHandler(
    IAiRagClient rag, IPatientRepository patients, IBreakGlassService breakGlass, IUnitOfWork uow, ICurrentUserContext ctx)
    : ICommandHandler<AskRagCommand, RagAnswerDto>
{
    public async Task<RagAnswerDto> Handle(AskRagCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        // ── Phase 1 — authorize in an own committed tenant scope, release before the AI hop.
        await using (var scope = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            if (!await patients.IsLinkedToTenantAsync(command.PatientId, command.TenantId, ct))
                throw new KeyNotFoundException("Patient not found in this tenant.");

            // RAG reads the patient's medical history → the SAME gate (resource_type medical_history) as the
            // history list; a patient-wide medical_history grant unlocks (the ask spans the patient's records).
            var patient = await patients.GetByIdAsync(command.PatientId, ct);
            if (patient is null || !patient.HasActiveConsent)
            {
                var grant = await breakGlass.GetActiveGrantAsync(userId, command.TenantId, command.PatientId, "medical_history", null, ct);
                if (grant is null)
                    throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
            }
            await scope.CommitAsync(ct);
        }

        // ── Phase 2 — ask the AI service OUTSIDE any DB tx. Strictly read-only (the client cannot index).
        var result = await rag.AskAsync(new RagAskInput(command.PatientId, command.Request.Question, command.DeclaredPurpose!), ct);

        if (result is null)
            return new RagAnswerDto(Available: false, command.PatientId, null, null, [], null);

        return new RagAnswerDto(
            Available: true, command.PatientId, result.Answer, result.Mode,
            result.Citations.Select(c => new RagCitationDto(c.HistoryId, c.RecordType, c.Title, c.Severity, c.Score)).ToList(),
            result.Retrieved, result.Source);
    }
}

// ---- Operational AI reads (non-PHI summaries): extraction list + RAG status -----------------------
// ISelfManagedTransaction: pure HTTP proxies with NO .NET DB work — the marker tells TenantScopeQueryBehavior
// NOT to open a tenant-scoped tx, so no pooled DB connection is held across the AI hop. The AI service scopes
// these by the JWT tenant (forwarded by the adapter); the responses carry NO individual PHI.

public sealed record ListAiExtractionsQuery(int Limit) : IQuery<OcrExtractionListDto>, ISelfManagedTransaction;

public sealed class ListAiExtractionsQueryHandler(IAiOcrClient ocr) : IQueryHandler<ListAiExtractionsQuery, OcrExtractionListDto>
{
    public async Task<OcrExtractionListDto> Handle(ListAiExtractionsQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var rows = await ocr.ListExtractionsAsync(limit, ct);
        if (rows is null) return new OcrExtractionListDto(Available: false, []);
        return new OcrExtractionListDto(
            Available: true,
            rows.Select(r => new OcrExtractionSummaryDto(
                r.ExtractionId, r.SourceType, r.Status, r.OverallConfidence, r.RequiresHumanReview, r.AbnormalCount, r.CreatedAt)).ToList(),
            rows.Count > 0 ? rows[0].Source : "ai-service-http");
    }
}

public sealed record GetRagStatusQuery : IQuery<RagStatusDto>, ISelfManagedTransaction;

public sealed class GetRagStatusQueryHandler(IAiRagClient rag) : IQueryHandler<GetRagStatusQuery, RagStatusDto>
{
    public async Task<RagStatusDto> Handle(GetRagStatusQuery query, CancellationToken ct)
    {
        var s = await rag.GetStatusAsync(ct);
        if (s is null) return new RagStatusDto(Available: false, null, null, []);
        return new RagStatusDto(
            Available: true, s.Embeddings, s.PatientsIndexed,
            s.KnowledgeBases.Select(k => new RagKnowledgeBaseDto(k.KbKey, k.Name, k.DocumentCount)).ToList(),
            s.Source);
    }
}
