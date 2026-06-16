using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.PlatformApi;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.PlatformApi.Webhooks;

// ---- Reads ---------------------------------------------------------------------------------------

public sealed record ListWebhooksQuery(Guid ClientId) : IQuery<IReadOnlyList<WebhookSubscriptionDto>>;

public sealed class ListWebhooksQueryHandler(IWebhookSubscriptionRepository repo)
    : IQueryHandler<ListWebhooksQuery, IReadOnlyList<WebhookSubscriptionDto>>
{
    public Task<IReadOnlyList<WebhookSubscriptionDto>> Handle(ListWebhooksQuery q, CancellationToken ct)
        => repo.ListByClientAsync(q.ClientId, ct);
}

public sealed record ListEventTypesQuery : IQuery<IReadOnlyList<EventTypeDto>>;

public sealed class ListEventTypesQueryHandler(IEventTypeRepository repo)
    : IQueryHandler<ListEventTypesQuery, IReadOnlyList<EventTypeDto>>
{
    public Task<IReadOnlyList<EventTypeDto>> Handle(ListEventTypesQuery q, CancellationToken ct) => repo.ListAsync(ct);
}

// ---- Create subscription -------------------------------------------------------------------------

public sealed record CreateWebhookCommand(CreateWebhookRequest Request) : ICommand<CreateWebhookResult>;

public sealed class CreateWebhookValidator : AbstractValidator<CreateWebhookCommand>
{
    public CreateWebhookValidator()
    {
        RuleFor(x => x.Request.ClientId).NotEmpty();
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Url).NotEmpty().Must(BeHttpsUrl)
            .WithMessage("Webhook URL must be an absolute https:// URL.");
        RuleFor(x => x.Request.EventTypes).NotEmpty().WithMessage("At least one event type is required.");
        RuleFor(x => x.Request.MaxRetries).InclusiveBetween((short)0, (short)20);
    }

    private static bool BeHttpsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}

public sealed class CreateWebhookCommandHandler(
    IWebhookSubscriptionRepository repo,
    IEventTypeRepository eventTypes,
    IWebhookSigner signer,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<CreateWebhookCommand, CreateWebhookResult>
{
    public async Task<CreateWebhookResult> Handle(CreateWebhookCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // Every subscribed event type must exist and be active (fail-closed).
        foreach (var et in req.EventTypes)
            if (!await eventTypes.ExistsAndActiveAsync(et, ct))
                throw new BusinessRuleException($"Unknown or inactive event type: {et}");

        // Use the caller-supplied secret or generate one; store it ENCRYPTED at rest (recoverable to sign
        // each delivery — never plaintext); return the plaintext to the caller exactly once.
        var secret = string.IsNullOrWhiteSpace(req.Secret)
            ? WebhookSecretGenerator.New()
            : req.Secret;
        var protectedSecret = signer.ProtectSecret(secret);

        var webhookId = await repo.CreateAsync(req, protectedSecret, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "webhook_subscription", webhookId, req.Name, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Webhook '{req.Name}' → {req.Url} for [{string.Join(',', req.EventTypes)}]"), ct);

        return new CreateWebhookResult(webhookId, secret);
    }
}

// ---- Update / delete -----------------------------------------------------------------------------

public sealed record UpdateWebhookCommand(Guid WebhookId, UpdateWebhookRequest Request) : ICommand<Unit>;

public sealed class UpdateWebhookCommandHandler(
    IWebhookSubscriptionRepository repo, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<UpdateWebhookCommand>
{
    public async Task<Unit> Handle(UpdateWebhookCommand command, CancellationToken ct)
    {
        var existing = await repo.GetByIdAsync(command.WebhookId, ct)
            ?? throw new KeyNotFoundException("Webhook subscription not found.");
        await repo.UpdateAsync(existing.WebhookId, command.Request, ct);
        await audit.RecordAsync(new AuditEntry(
            "update", "webhook_subscription", existing.WebhookId, existing.Name, ctx.UserId, existing.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Webhook updated"), ct);
        return Unit.Value;
    }
}

public sealed record DeleteWebhookCommand(Guid WebhookId) : ICommand<Unit>;

public sealed class DeleteWebhookCommandHandler(
    IWebhookSubscriptionRepository repo, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<DeleteWebhookCommand>
{
    public async Task<Unit> Handle(DeleteWebhookCommand command, CancellationToken ct)
    {
        var existing = await repo.GetByIdAsync(command.WebhookId, ct)
            ?? throw new KeyNotFoundException("Webhook subscription not found.");
        await repo.DeleteAsync(existing.WebhookId, ct);
        await audit.RecordAsync(new AuditEntry(
            "delete", "webhook_subscription", existing.WebhookId, existing.Name, ctx.UserId, existing.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Webhook deleted"), ct);
        return Unit.Value;
    }
}

// ---- Publish a (synthetic, slice-02) integration event through the pipeline -----------------------

/// <summary>
/// Publishes an integration event to all matching subscriptions (sign → deliver → retry). In slice 02 the
/// event source is synthetic (a test/admin trigger); slice 03's docslot domain events feed this seam.
/// Returns the delivery ids created.
/// </summary>
public sealed record PublishEventCommand(string EventType, Guid? TenantId, string PayloadJson)
    : ICommand<IReadOnlyList<Guid>>;

public sealed class PublishEventValidator : AbstractValidator<PublishEventCommand>
{
    public PublishEventValidator()
    {
        RuleFor(x => x.EventType).NotEmpty();
        RuleFor(x => x.PayloadJson).NotEmpty();
    }
}

public sealed class PublishEventCommandHandler(
    IEventTypeRepository eventTypes, IWebhookPublisher publisher, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<PublishEventCommand, IReadOnlyList<Guid>>
{
    public async Task<IReadOnlyList<Guid>> Handle(PublishEventCommand command, CancellationToken ct)
    {
        if (!await eventTypes.ExistsAndActiveAsync(command.EventType, ct))
            throw new BusinessRuleException($"Unknown or inactive event type: {command.EventType}");

        var evt = new IntegrationEvent(
            Guid.CreateVersion7(), command.EventType, command.TenantId,
            command.PayloadJson, ctx.CorrelationId, clock.UtcNow);

        return await publisher.PublishAsync(evt, ct);
    }
}

internal static class WebhookSecretGenerator
{
    public static string New() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
