using System.Text;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Admin;

// ============================================================================
// People — export + bulk import (issue #95, Phase D of epic #80).
//   • Export  : GET  /tenants/{id}/users/export  — CSV of the tenant's members (staff PII, not PHI).
//   • Import  : POST /tenants/{id}/users/bulk-import — provision N rows through the SAME escalation-safe
//               single-user path, per-row atomic, one bad row never aborts the batch.
// Tenant is ALWAYS bound from the server-signed ICurrentUserContext — the route id is cross-checked, never
// trusted. Role assignment re-uses assign_role_to_user's R3 no-escalation guard, so a bulk row can never
// confer a role the actor couldn't confer singly.
// ============================================================================

// ---- Export the tenant's members to CSV (query) --------------------------------------------------

/// <summary>Exports the caller's tenant members as CSV. Tenant is bound from the signed context; the route
/// id must match it (defense in depth). Runs on the read chain (tenant-scoped, rolled-back read tx).</summary>
public sealed record ExportTenantUsersQuery(Guid TenantId) : IQuery<PeopleExportResult>;

public sealed class ExportTenantUsersQueryHandler(IUserDirectory directory, ICurrentUserContext ctx)
    : IQueryHandler<ExportTenantUsersQuery, PeopleExportResult>
{
    private const int PageSize = 200;   // IUserDirectory clamps take to 200
    private const int MaxRows = 20_000; // hard cap so an export can never fan out unbounded

    public async Task<PeopleExportResult> Handle(ExportTenantUsersQuery query, CancellationToken ct)
    {
        // Bind the tenant STRICTLY from the signed context; the route id is only an address. A mismatch (or a
        // platform actor with no tenant) is refused — the export can only ever be the caller's own tenant.
        if (ctx.TenantId is null || query.TenantId != ctx.TenantId)
            throw new ForbiddenException("Tenant mismatch.");

        var tenantId = ctx.TenantId.Value;
        var members = new List<UserListItemDto>();
        for (var skip = 0; skip < MaxRows; skip += PageSize)
        {
            var page = await directory.ListByTenantAsync(tenantId, skip, PageSize, ct);
            members.AddRange(page);
            if (page.Count < PageSize) break;   // last page reached
        }

        var fileName = $"people-{tenantId:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return new PeopleExportResult(fileName, PeopleCsv.Build(members));
    }
}

/// <summary>Assembles the People CSV. Staff PII only (name/email/roles/org/status/2FA/last-active) — no PHI,
/// no secrets, no auth material. CSV-injection-safe: RFC-4180 quoting AND leading =,+,-,@ neutralisation
/// (mirrors <c>SecurityFeatures.AuditCsv</c>).</summary>
public static class PeopleCsv
{
    private static readonly string[] Header =
        ["full_name", "email", "roles", "branch", "department", "status", "two_factor", "last_active"];

    public static string Build(IReadOnlyList<UserListItemDto> members)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Header));
        foreach (var m in members)
        {
            var roles = string.Join("; ", m.Roles.Select(r => r.Name));
            var lastActive = (m.LastActivityAt ?? m.LastLoginAt)?.ToString("o") ?? "";
            sb.AppendLine(string.Join(',', new[]
            {
                Csv(m.FullName), Csv(m.Email), Csv(roles), Csv(m.BranchName), Csv(m.Department),
                m.IsActive ? "Active" : "Inactive", m.MfaEnabled ? "Enabled" : "Disabled", Csv(lastActive),
            }));
        }
        return sb.ToString();
    }

    /// <summary>RFC-4180 field quoting; also neutralises a leading =,+,-,@ to defuse CSV/formula injection.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r'))
            v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}

// ---- Bulk import members (command) ---------------------------------------------------------------

public sealed record BulkImportUsersCommand(Guid TenantId, BulkImportUsersRequest Request)
    : ICommand<BulkImportResult>;

public sealed class BulkImportUsersValidator : AbstractValidator<BulkImportUsersCommand>
{
    /// <summary>Max rows accepted in one import. Oversize is rejected with 422 (never partially processed).</summary>
    public const int MaxBatch = 500;

    public BulkImportUsersValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.Rows).NotNull().WithMessage("A row list is required.");
        RuleFor(x => x.Request.Rows)
            .Must(r => r is { Count: >= 1 }).WithMessage("At least one row is required.")
            .Must(r => r is null || r.Count <= MaxBatch)
            .WithMessage($"Batch too large: at most {MaxBatch} rows per import.")
            .When(x => x.Request.Rows is not null);
    }
}

/// <summary>
/// Provisions each row through the SAME path as the single-user create (<c>IUserProvisioning</c> + the
/// escalation-safe <c>assign_role_to_user</c>), but per-row atomic: a SAVEPOINT wraps each row so a failing
/// row (e.g. the R3 no-escalation guard raising 42501 → <see cref="ForbiddenException"/>, which also aborts the
/// PostgreSQL transaction) is rolled back in isolation — the orphan user for that row is undone AND the batch
/// continues. Valid rows still commit. A bulk row can therefore never confer a role the actor couldn't confer
/// singly, and one bad row never poisons the whole import.
/// </summary>
public sealed class BulkImportUsersCommandHandler(
    IUserProvisioning provisioning, IRoleAssignmentRepository roles, IUnitOfWork uow,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<BulkImportUsersCommand, BulkImportResult>
{
    public async Task<BulkImportResult> Handle(BulkImportUsersCommand command, CancellationToken ct)
    {
        // Tenant is the server-signed context; the route id is only an address (the RequirePermission gate
        // already resolved the caller's perms in ctx.TenantId). A mismatch is refused.
        if (ctx.TenantId is null || command.TenantId != ctx.TenantId)
            throw new ForbiddenException("Tenant mismatch.");
        var tenantId = ctx.TenantId.Value;

        // Prefetch roles visible in this tenant scope (system + own custom) once, keyed by role_key. Resolution
        // does NOT authorise — assign_role_to_user still enforces the escalation guard per row.
        var roleList = await roles.ListRolesAsync(tenantId, ct);
        var rolesByKey = roleList
            .GroupBy(r => r.RoleKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().RoleId, StringComparer.OrdinalIgnoreCase);

        var results = new List<BulkImportRowResult>(command.Request.Rows.Count);
        var rowNum = 0;
        foreach (var row in command.Request.Rows)
        {
            rowNum++;
            var email = row.Email?.Trim() ?? "";
            var fullName = row.FullName?.Trim() ?? "";
            var roleKey = string.IsNullOrWhiteSpace(row.RoleKey) ? null : row.RoleKey.Trim();

            if (email.Length == 0 || fullName.Length == 0)
            {
                results.Add(new BulkImportRowResult(rowNum, email, "skipped", "Email and full name are required."));
                continue;
            }
            // Match the single-user path's server-side row validation (audit LOW): reject a malformed email or an
            // over-length name up front rather than letting it reach provisioning.
            if (fullName.Length > 200 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            {
                results.Add(new BulkImportRowResult(rowNum, email, "error", "Invalid email address, or full name exceeds 200 characters."));
                continue;
            }

            var savepoint = $"bulk_row_{rowNum}";
            await uow.CreateSavepointAsync(savepoint, ct);
            try
            {
                // Resolve the role (if any) up-front so an unknown key doesn't mint an orphan user.
                Guid? roleId = null;
                if (roleKey is not null)
                {
                    if (!rolesByKey.TryGetValue(roleKey, out var rid))
                    {
                        await uow.RollbackToSavepointAsync(savepoint, ct);
                        results.Add(new BulkImportRowResult(rowNum, email, "error", $"Unknown role '{roleKey}'."));
                        continue;
                    }
                    roleId = rid;
                }

                var (userId, alreadyExisted) = await provisioning.CreateAsync(
                    new CreateUserRequest(email, fullName, Phone: null), clock.UtcNow, ct);

                // R3: route the role assignment through assign_role_to_user — the actor may only confer a role
                // whose permissions they hold WITH grant option. A 42501 surfaces as ForbiddenException below.
                if (roleId is { } confer)
                    await roles.AssignRoleAsync(ctx.UserId!.Value, userId, confer, tenantId, ct);

                await audit.RecordAsync(new AuditEntry(
                    alreadyExisted ? "link_user" : "create", "user", userId, email, ctx.UserId, tenantId,
                    ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                    ChangeSummary: $"Bulk import: {(alreadyExisted ? "linked" : "created")} {email}"
                                   + (roleKey is null ? "" : $" with role {roleKey}")), ct);

                results.Add(new BulkImportRowResult(rowNum, email, alreadyExisted ? "linked" : "created", null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A DB error (e.g. the R3 escalation guard's 42501) aborts the PG transaction; roll back to the
                // savepoint to un-abort it so the remaining rows still run — this row's provisioning + assignment
                // are undone together (per-row atomicity).
                await uow.RollbackToSavepointAsync(savepoint, ct);
                // Audit the REJECTED row (Success=false) on the dedicated audit connection (survives the savepoint
                // rollback) so a blocked escalation / provisioning failure via the bulk surface is DETECTABLE — a
                // silently-dropped privilege-escalation attempt must not be invisible (audit MEDIUM).
                await audit.RecordAsync(new AuditEntry(
                    "create", "user", Guid.Empty, email, ctx.UserId, tenantId,
                    ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                    ChangeSummary: $"Bulk import rejected {email}"
                                   + (roleKey is null ? "" : $" (role {roleKey})") + $": {SafeError(ex)}"), ct);
                results.Add(new BulkImportRowResult(rowNum, email, "error", SafeError(ex)));
            }
        }

        return new BulkImportResult(
            results.Count,
            results.Count(r => r.Status == "created"),
            results.Count(r => r.Status == "linked"),
            results.Count(r => r.Status == "skipped"),
            results.Count(r => r.Status == "error"),
            results);
    }

    // Domain exceptions carry translated, client-safe messages; anything else is masked to a generic string
    // (the real detail is captured in the Success=false audit above and logged server-side) — audit LOW.
    private static string SafeError(Exception ex) =>
        ex is ForbiddenException or ConflictException ? ex.Message : "Row could not be provisioned.";
}
