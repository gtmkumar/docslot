using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Commission;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

internal static class BrokerFields
{
    public static readonly FieldRef Pan = new("commission", "brokers", "pan_number");
    public const string CarePartnerLabel = "Care Partner";   // MCI 6.4 — customer-facing term, never "Referral Partner"
}

// ---- Register broker (PAN encrypted at rest) -----------------------------------------------------

public sealed record RegisterBrokerCommand(Guid TenantId, RegisterBrokerRequest Request) : ICommand<RegisterBrokerResult>;

public sealed class RegisterBrokerValidator : AbstractValidator<RegisterBrokerCommand>
{
    private static readonly string[] Types =
        ["medical_rep", "corporate_hr", "insurance_panel", "aggregator_agent", "community_worker", "hotel_concierge", "individual", "platform_partner"];

    public RegisterBrokerValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.Phone).NotEmpty().MaximumLength(15);
        RuleFor(x => x.Request.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.BrokerType).Must(t => Types.Contains(t)).WithMessage("Invalid broker_type.");
        RuleFor(x => x.Request.Pan).Matches("^[A-Z]{5}[0-9]{4}[A-Z]$").When(x => !string.IsNullOrEmpty(x.Request.Pan))
            .WithMessage("PAN must match the AAAAA9999A format.");
    }
}

public sealed class RegisterBrokerCommandHandler(
    IBrokerRepository brokers, IFieldEncryptionService encryption, IBrokerWalletRepository wallets,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<RegisterBrokerCommand, RegisterBrokerResult>
{
    public async Task<RegisterBrokerResult> Handle(RegisterBrokerCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var now = clock.UtcNow;

        // Phone-canonical identity: reuse an existing broker if the phone is known.
        var existing = await brokers.GetByPhoneAsync(req.Phone, ct);
        if (existing is not null)
        {
            if (!await brokers.IsLinkedToTenantAsync(existing.BrokerId, command.TenantId, ct))
                await brokers.LinkToTenantAsync(existing.BrokerId, command.TenantId, now, ct);
            return new RegisterBrokerResult(existing.BrokerId, AlreadyExisted: true);
        }

        // PAN encrypted at rest (registry: data_class=tax_id, legal_obligation). Plaintext never persisted.
        string? panEnc = null;
        if (!string.IsNullOrEmpty(req.Pan))
            panEnc = await encryption.EncryptAsync(BrokerFields.Pan, command.TenantId,
                req.Pan, new EncryptionContext(ctx.UserId, command.TenantId, "broker", null, ctx.IpAddress), ct);

        var broker = Broker.Register(req.Phone, req.FullName, req.Email, req.BrokerType, panEnc, req.GstNumber, req.OnboardedVia, now);
        var brokerId = await brokers.CreateAsync(broker, ct);
        await brokers.LinkToTenantAsync(brokerId, command.TenantId, now, ct);
        await wallets.EnsureExistsAsync(brokerId, ct);

        await audit.RecordAsync(new AuditEntry(
            "register", "broker", brokerId, req.FullName, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Registered Care Partner {req.FullName} ({req.BrokerType})"), ct);

        return new RegisterBrokerResult(brokerId, AlreadyExisted: false);
    }
}

// ---- Activate / suspend (tenant) -----------------------------------------------------------------

public sealed record SetBrokerStatusCommand(Guid TenantId, Guid BrokerId, SetBrokerStatusRequest Request) : ICommand<Unit>;

public sealed class SetBrokerStatusCommandHandler(
    IBrokerRepository brokers, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<SetBrokerStatusCommand>
{
    public async Task<Unit> Handle(SetBrokerStatusCommand command, CancellationToken ct)
    {
        var broker = await brokers.GetByIdAsync(command.BrokerId, ct) ?? throw new KeyNotFoundException("Broker not found.");
        await brokers.SetActiveAsync(broker.BrokerId, command.TenantId, command.Request.IsActive, ctx.UserId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "set_status", "broker", broker.BrokerId, broker.FullName, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"active={command.Request.IsActive}. {command.Request.Reason}"), ct);
        return Unit.Value;
    }
}

// ---- Blacklist (platform-level, dangerous) -------------------------------------------------------

public sealed record BlacklistBrokerCommand(Guid BrokerId, BlacklistBrokerRequest Request) : ICommand<Unit>;

public sealed class BlacklistBrokerValidator : AbstractValidator<BlacklistBrokerCommand>
{
    public BlacklistBrokerValidator() => RuleFor(x => x.Request.Reason).NotEmpty().WithMessage("A blacklist reason is mandatory.");
}

public sealed class BlacklistBrokerCommandHandler(
    IBrokerRepository brokers, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<BlacklistBrokerCommand>
{
    public async Task<Unit> Handle(BlacklistBrokerCommand command, CancellationToken ct)
    {
        var broker = await brokers.GetByIdAsync(command.BrokerId, ct) ?? throw new KeyNotFoundException("Broker not found.");
        await brokers.BlacklistAsync(broker.BrokerId, command.Request.Reason, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "blacklist", "broker", broker.BrokerId, broker.FullName, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: $"Blacklisted: {command.Request.Reason}"), ct);
        return Unit.Value;
    }
}

// ---- List brokers / wallet (reads) ---------------------------------------------------------------

public sealed record ListBrokersQuery(Guid TenantId, int Skip, int Take) : IQuery<IReadOnlyList<BrokerDto>>;

public sealed class ListBrokersQueryHandler(IBrokerRepository brokers)
    : IQueryHandler<ListBrokersQuery, IReadOnlyList<BrokerDto>>
{
    public Task<IReadOnlyList<BrokerDto>> Handle(ListBrokersQuery q, CancellationToken ct)
        => brokers.ListByTenantAsync(q.TenantId, q.Skip, Math.Clamp(q.Take, 1, 200), ct);
}

public sealed record GetBrokerWalletQuery(Guid BrokerId) : IQuery<BrokerWalletDto>;

public sealed class GetBrokerWalletQueryHandler(IBrokerWalletRepository wallets)
    : IQueryHandler<GetBrokerWalletQuery, BrokerWalletDto>
{
    public async Task<BrokerWalletDto> Handle(GetBrokerWalletQuery q, CancellationToken ct)
        => await wallets.GetAsync(q.BrokerId, ct) ?? throw new KeyNotFoundException("Wallet not found.");
}
