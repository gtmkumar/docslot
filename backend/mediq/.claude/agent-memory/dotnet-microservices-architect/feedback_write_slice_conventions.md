---
name: write-slice-conventions
description: How a create/write vertical slice is structured in the mediq .NET solution (CQRS without MediatR, UoW tenant scope, idempotency, audit).
metadata:
  type: feedback
---

A create endpoint in this solution mirrors the register-patient / create-booking slices. Structure:

- **Command + Request + Result + Validator + Handler** all co-located in ONE file under `mediq.Application/Features/Docslot/<Area>/<Name>Command.cs`. Request/Result are records in the SAME file (not in SharedDataModel — only enums/shared DTOs live there).
- Command shape: `record XCommand(Guid TenantId, XRequest Request) : ICommand<XResult>`. Controller passes `RequireTenant()` (from `ICurrentUserContext.TenantId`, the JWT claim) — tenant_id NEVER from a header.
- **Handlers + FluentValidation validators are auto-scanned** by `ApplicationRegistration.AddApplication` (assembly scan of closed `ICommandHandler<,>`/`IQueryHandler<,>` + `AddValidatorsFromAssembly`). No manual handler registration needed.
- Command pipeline order (DI in `ApplicationRegistration`): Logging → Validation → Idempotency → UnitOfWork → DomainExceptionTranslation → handler. The `UnitOfWorkBehavior` opens the tenant-scoped tx (`SET LOCAL app.tenant_id`) so RLS applies and writes commit atomically. Repositories using the same `PlatformDbContext` (e.g. `ExecuteSqlRawAsync`) automatically enlist.
- **Idempotency:** honoured if an `Idempotency-Key` header is present (behavior caches first response); only REQUIRED when the command implements `IRequireIdempotency` (booking/money mutations). A plain create (patient/doctor) does NOT implement it — key optional.
- **Repository pattern only where it earns it:** write-side gets a thin repo (e.g. `IDoctorRepository.CreateAsync`) to keep raw SQL out of the handler; reads project off `IDoctorReadService`/DbContext. Register repos in `mediq.Infrastructure/DependencyInjection.cs`.
- **Audit:** handler calls `IAuditTrailWriter.RecordAsync(new AuditEntry("create", "<resourceType>", id, label, ctx.UserId, tenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success:true, ChangeSummary:...))`.
- **Controller:** `[RequirePermission("docslot.<x>.create")]`, returns `CreatedAtAction(...)` (201). `[ApiController]`+`[Authorize]`.
- **Enums:** use SharedDataModel enums (e.g. `Gender`) string-serialized via `EnumMemberJsonConverter`. To turn a bound enum into its snake_case DB token inside a handler, use `EnumMemberTokens.ToWireToken()` (added in `mediq.SharedDataModel/Json/EnumMemberTokens.cs`).
- **Database-first:** map only to columns that exist in `database/03_docslot.sql`; let NOT-NULL-with-DEFAULT columns (e.g. doctors.role='doctor', doctors.qualifications='[]'::jsonb) fall back to the DB default by omitting them from the INSERT column list.
