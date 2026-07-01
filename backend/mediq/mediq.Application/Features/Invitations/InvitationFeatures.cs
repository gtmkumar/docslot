using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Invitations;

// ============================================================================
// Invitations (issue #89, epic #80 Phase C) — token-based tenant onboarding.
// A NEW capability alongside the direct-add invite (Admin CreateUser). An admin
// mints a single-use hashed token (returned in plaintext ONCE); the invitee
// redeems it unauthenticated to self-provision + receive the pre-vetted role.
// Writes route through the SECURITY DEFINER functions, which enforce the actor's
// tenant.users.create + the R3 no-escalation guard at the database. The actor +
// tenant are ALWAYS the server-signed principal — never a body/route value.
// ============================================================================

/// <summary>How long a freshly-minted or resent invitation stays valid.</summary>
internal static class InvitationPolicy
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
}

/// <summary>
/// ADVISORY dispatch of the invite link through <see cref="IInvitationNotifier"/> (issue #93). Best-effort and
/// NON-BLOCKING: any notifier failure is swallowed + logged (warning) so the invitation write still commits and
/// the one-time token is still returned to the admin. The token is a live credential — never logged here.
/// </summary>
internal static class InvitationNotify
{
    public static async Task AdvisoryAsync(
        IInvitationNotifier notifier, ILogger logger, InvitationNotification notification, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(notification, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The invite already exists + the token was returned — delivery is advisory, so a notifier failure
            // must NOT surface to the caller. Log the failure WITHOUT the token/link.
            logger.LogWarning(ex,
                "Invitation notifier failed for invitation={InvitationId} tenant={TenantId} (advisory — invite still created)",
                notification.InvitationId, notification.TenantId);
        }
    }
}

// ---- List invitations (query) --------------------------------------------------------------------

/// <summary>Lists a tenant's invitations (optionally filtered by status). Never returns the token/hash.
/// Tenant is bound from the signed context; RLS + the explicit predicate bound it to the caller's tenant.</summary>
public sealed record ListInvitationsQuery(string? Status) : IQuery<InvitationListDto>;

public sealed class ListInvitationsQueryHandler(IInvitationRepository invitations, ICurrentUserContext ctx)
    : IQueryHandler<ListInvitationsQuery, InvitationListDto>
{
    public async Task<InvitationListDto> Handle(ListInvitationsQuery query, CancellationToken ct)
    {
        var tenantId = ctx.TenantId
            ?? throw new ForbiddenException("A tenant context is required to list invitations.");
        var items = await invitations.ListAsync(tenantId, query.Status, ct);
        return new InvitationListDto(items, items.Count);
    }
}

// ---- Create invitation (command) -----------------------------------------------------------------

/// <summary>Mint a pending invitation. <c>RouteTenantId</c> is the URL tenant, validated to equal the
/// signed-context tenant so the URL can never target another tenant.</summary>
public sealed record CreateInvitationCommand(Guid RouteTenantId, CreateInvitationRequest Request)
    : ICommand<InvitationTokenResult>, IDoNotCacheResponse;   // one-time plaintext token → never idempotency-cached

public sealed class CreateInvitationValidator : AbstractValidator<CreateInvitationCommand>
{
    public CreateInvitationValidator()
    {
        RuleFor(x => x.RouteTenantId).NotEmpty();
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(255);
    }
}

public sealed class CreateInvitationCommandHandler(
    IInvitationRepository invitations, IInvitationTokenFactory tokens,
    IAuditTrailWriter audit, IInvitationNotifier notifier, ILogger<CreateInvitationCommandHandler> logger,
    ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateInvitationCommand, InvitationTokenResult>
{
    public async Task<InvitationTokenResult> Handle(CreateInvitationCommand command, CancellationToken ct)
    {
        var tenantId = ResolveTenant(ctx, command.RouteTenantId);
        var (token, tokenHash) = tokens.Create();
        var expiresAt = clock.UtcNow.Add(InvitationPolicy.Ttl);

        // create_invitation (SECURITY DEFINER) enforces tenant.users.create + the R3 no-escalation guard on the
        // optional role (→ 403 on 42501) and the one-live-pending-per-email rule (→ 409 on 23505).
        var invitationId = await invitations.CreateAsync(
            ctx.UserId!.Value, tenantId, command.Request.Email, command.Request.RoleId, tokenHash, expiresAt, ct);

        // Audit records the EMAIL + role, never the token/hash (the token is a live credential).
        await audit.RecordAsync(new AuditEntry(
            "create", "invitation", invitationId, command.Request.Email, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Invited {command.Request.Email} to tenant {tenantId}"
                + (command.Request.RoleId is { } r ? $" with role {r}" : " (no role)")), ct);

        // #93: hand the one-time link to the (offline-by-default) notifier. Advisory — a failure never fails the invite.
        await InvitationNotify.AdvisoryAsync(notifier, logger,
            new InvitationNotification(invitationId, tenantId, command.Request.Email, token, expiresAt, IsResend: false), ct);

        return new InvitationTokenResult(invitationId, token, expiresAt, ResendCount: 0);
    }

    internal static Guid ResolveTenant(ICurrentUserContext ctx, Guid routeTenantId)
    {
        var tenantId = ctx.TenantId
            ?? throw new ForbiddenException("A tenant context is required to manage invitations.");
        if (routeTenantId != tenantId)
            throw new ForbiddenException("The URL tenant does not match your active tenant.");
        return tenantId;
    }
}

// ---- Resend invitation (command) -----------------------------------------------------------------

/// <summary>Rotate the token + extend expiry, bumping resend_count. Returns a NEW one-time token.</summary>
public sealed record ResendInvitationCommand(Guid RouteTenantId, Guid InvitationId)
    : ICommand<InvitationTokenResult>, IDoNotCacheResponse;

public sealed class ResendInvitationCommandHandler(
    IInvitationRepository invitations, IInvitationTokenFactory tokens,
    IAuditTrailWriter audit, IInvitationNotifier notifier, ILogger<ResendInvitationCommandHandler> logger,
    ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ResendInvitationCommand, InvitationTokenResult>
{
    public async Task<InvitationTokenResult> Handle(ResendInvitationCommand command, CancellationToken ct)
    {
        var tenantId = CreateInvitationCommandHandler.ResolveTenant(ctx, command.RouteTenantId);
        var (token, tokenHash) = tokens.Create();
        var expiresAt = clock.UtcNow.Add(InvitationPolicy.Ttl);

        // resend_invitation rotates the hash (invalidating the prior token), extends expiry, +1 resend_count.
        // Pending-only (→ 422 on P0002 if not pending). Needs tenant.users.create (→ 403 on 42501).
        await invitations.ResendAsync(ctx.UserId!.Value, tenantId, command.InvitationId, tokenHash, expiresAt, ct);

        var list = await invitations.ListAsync(tenantId, null, ct);
        var row = list.FirstOrDefault(i => i.InvitationId == command.InvitationId);
        var resendCount = row?.ResendCount ?? 0;

        await audit.RecordAsync(new AuditEntry(
            "resend", "invitation", command.InvitationId, null, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Resent invitation {command.InvitationId} (new token)"), ct);

        // #93: re-deliver the rotated one-time link. Advisory — a failure never fails the resend.
        await InvitationNotify.AdvisoryAsync(notifier, logger,
            new InvitationNotification(command.InvitationId, tenantId, row?.InvitedEmail ?? string.Empty, token, expiresAt, IsResend: true), ct);

        return new InvitationTokenResult(command.InvitationId, token, expiresAt, resendCount);
    }
}

// ---- Revoke invitation (command) -----------------------------------------------------------------

/// <summary>Cancel a pending invitation (status=revoked). Idempotent.</summary>
public sealed record RevokeInvitationCommand(Guid RouteTenantId, Guid InvitationId)
    : ICommand<RevokeInvitationResult>;

public sealed class RevokeInvitationCommandHandler(
    IInvitationRepository invitations, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeInvitationCommand, RevokeInvitationResult>
{
    public async Task<RevokeInvitationResult> Handle(RevokeInvitationCommand command, CancellationToken ct)
    {
        var tenantId = CreateInvitationCommandHandler.ResolveTenant(ctx, command.RouteTenantId);

        // revoke_invitation returns false when it was not pending (already accepted/revoked/expired).
        var didRevoke = await invitations.RevokeAsync(ctx.UserId!.Value, tenantId, command.InvitationId, ct);

        if (didRevoke)
            await audit.RecordAsync(new AuditEntry(
                "revoke", "invitation", command.InvitationId, null, ctx.UserId, tenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: $"Revoked invitation {command.InvitationId}"), ct);

        return new RevokeInvitationResult(command.InvitationId, AlreadyInactive: !didRevoke);
    }
}

// ---- Accept invitation (command) — UNAUTHENTICATED, the token is the authorization ---------------

/// <summary>Redeem a token: provision/link the user, assign the pre-vetted role, mark accepted. Single-use.
/// No JWT — the token IS the authorization; the handler resolves everything from the token hash.</summary>
public sealed record AcceptInvitationCommand(AcceptInvitationRequest Request)
    : ICommand<AcceptInvitationResult>, IDoNotCacheResponse;   // single-use; nothing to replay-cache

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationValidator()
    {
        RuleFor(x => x.Request.Token).NotEmpty();
        RuleFor(x => x.Request.DisplayName).NotEmpty().MaximumLength(200);
        // A real password floor — the invitee is setting their permanent credential here.
        RuleFor(x => x.Request.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class AcceptInvitationCommandHandler(
    IInvitationRepository invitations, IInvitationTokenFactory tokens, IPasswordHasher hasher,
    IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<AcceptInvitationCommand, AcceptInvitationResult>
{
    public async Task<AcceptInvitationResult> Handle(AcceptInvitationCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var tokenHash = tokens.Hash(req.Token);          // look up by HASH — the plaintext is never stored
        var passwordHash = hasher.Hash(req.Password);     // argon2id; the DB stores only the hash

        // accept_invitation (SECURITY DEFINER, unauthenticated) validates the token is a live pending invite,
        // provisions-or-links the user, assigns the pre-vetted role, and flips it to accepted — atomically.
        // Any invalid/expired/revoked/used token surfaces as ONE generic 422 (no enumeration).
        var (userId, tenantId, alreadyExisted) = await invitations.AcceptAsync(
            tokenHash, passwordHash, req.DisplayName.Trim(), ct);

        // Audit the acceptance with a NULL actor: the request is unauthenticated (the token is the
        // authorization) AND the audit writer uses a dedicated connection that commits independently — it
        // cannot reference the just-provisioned user (still uncommitted in this request's transaction) without
        // tripping audit_log.user_id's FK. The provisioned user + tenant live in the resource fields instead.
        await audit.RecordAsync(new AuditEntry(
            "accept", "invitation", userId, tenantId.ToString(), UserId: null, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Invitation accepted; user {userId} joined tenant {tenantId}"
                + (alreadyExisted ? " (existing identity linked)" : " (new user provisioned)")), ct);

        return new AcceptInvitationResult(userId, tenantId, alreadyExisted);
    }
}
