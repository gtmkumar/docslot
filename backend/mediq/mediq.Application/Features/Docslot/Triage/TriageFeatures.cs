using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Triage;

namespace mediq.Application.Features.Docslot.Triage;

// ---- Submit a complaint for AI triage (proxies the AI sibling-service /triage workflow) -----------

// ISelfManagedTransaction: the handler does an EXTERNAL HTTP call (the AI service) and NO .NET DB work, so it
// must not be wrapped in the UnitOfWork transaction (no pooled DB connection held across the network call).
// IDoNotCacheResponse: the result is a clinical assessment (urgency / red flags) — never persist it to the
// plaintext idempotency store. NOT IRequireIdempotency (triage is advisory; re-submitting is harmless).
public sealed record SubmitTriageCommand(Guid TenantId, TriageRequest Request, string? DeclaredPurpose)
    : ICommand<TriageResultDto>, ISelfManagedTransaction, IDoNotCacheResponse;

public sealed class SubmitTriageValidator : AbstractValidator<SubmitTriageCommand>
{
    public SubmitTriageValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.Complaint).NotEmpty().MaximumLength(4000)   // bound the PHI payload
            .WithMessage("A symptom complaint (1–4000 chars) is required for triage.");
        // DPDP: a triage bound to a patient/booking is a patient-data access → X-Purpose-Of-Use is REQUIRED
        // (mirrors the clinical PHI reads; the AI service also enforces this and writes the purpose-of-use log).
        // A pure free-text triage (no patient/booking binding) does not require a purpose.
        RuleFor(x => x.DeclaredPurpose).NotEmpty()
            .When(x => x.Request.PatientId is not null || x.Request.BookingId is not null)
            .WithMessage("A declared purpose-of-use (X-Purpose-Of-Use header) is required to triage against a patient/booking (DPDP).");
    }
}

public sealed class SubmitTriageCommandHandler(IAiTriageClient triage)
    : ICommandHandler<SubmitTriageCommand, TriageResultDto>
{
    public async Task<TriageResultDto> Handle(SubmitTriageCommand command, CancellationToken ct)
    {
        var r = command.Request;
        // The complaint (PHI) is forwarded to the AI sibling and is NEVER logged here. The declared purpose is
        // forwarded too so the AI service's DPDP purpose-of-use gate + log fire on the patient-bound path.
        var result = await triage.TriageAsync(
            new TriageRequestInput(r.Complaint, r.PatientId, r.BookingId, r.PatientAge, command.DeclaredPurpose), ct);

        if (result is null)
            return new TriageResultDto(Available: false, null, null, [], [], [], null);

        return new TriageResultDto(
            Available: true, result.UrgencyBand, result.Department,
            result.RedFlags, result.Symptoms,
            result.SuggestedDoctors
                .Select(d => new SuggestedDoctorDto(d.DoctorId, d.FullName, d.Specialization, d.ConsultationFee, d.NextAvailableSlot))
                .ToList(),
            result.RunId, result.Source);
    }
}
