---
name: slice01-platform-core
description: DocSlot .NET slice 01 (platform_core) topology, custom CQRS shape, EF database-first mapping, RBAC/auth conventions established in backend/mediq.
metadata:
  type: project
---

Slice 01 (`platform_core`) scaffolded the whole .NET backend topology AND implemented identity/auth/RBAC against the canonical `docslot_platform` Postgres DB.

**Why:** Foundation slice — everything later builds on this CQRS + Clean-Arch + RBAC seam.
**How to apply:** Reuse these patterns for slices 02/03/07; do not re-scaffold or duplicate.

## Topology (in `backend/mediq/mediq.sln`)
Projects: `mediq.Domain` (no external refs) ← `mediq.Application` (custom CQRS + abstractions + handlers; refs Domain, SharedDataModel, Utilities) ← `mediq.Infrastructure` (EF Core 10, security, RBAC, audit) ← `mediq.Api` (controllers, JWT, middleware) + `mediq.Gateway` (YARP) + `mediq.AppHost` (Aspire) + `mediq.ServiceDefaults` + `tests/mediq.IntegrationTests`. Pre-existing `mediq.Utilities` + `mediq.SharedDataModel` are reused, not modified structurally.

## Custom CQRS (NO MediatR) — lives in `mediq.Application/Cqrs/`
`ICommand<T>`/`ICommand`/`IQuery<T>`, `ICommandHandler<,>`/`IQueryHandler<,>`, `ICommandDispatcher`/`IQueryDispatcher` (dispatch via closed-generic + `dynamic`). Two behavior chains: command = Logging→Validation→Idempotency→UnitOfWork; query = Logging only. `Unit` struct for void commands. Registered by assembly scan in `ApplicationRegistration.AddApplication()`. Validation behavior throws the Utilities `ValidationException` (DRY) → existing `ExceptionHandler` maps to 422.

## EF database-first (`mediq.Infrastructure/Persistence`)
`PlatformDbContext` maps ONLY the platform_core tables slice 01 needs (HasDefaultSchema "platform"). Hand-authored `IEntityTypeConfiguration<T>` (no scaffold dump). **GOTCHA: Npgsql `inet` columns** (users.last_login_ip, user_sessions.ip_address, login_attempts.ip_address) need a string↔IPAddress `ValueConverter` (`InetConverters` in PlatformConfigurations.cs) or EF model validation throws. `citext` maps to string fine. RBAC SQL functions (`resolve_user_permissions`, `get_user_menus`, `user_has_permission`) invoked via `FromSqlRaw` against keyless projection types — never reimplemented in C#. Session/audit/login-attempt/user-insert writes use parameterized raw SQL with `CAST(@p AS inet)`.

## Repository/UoW placement
Repositories only where they earn it: `UserRepository` (lockout state), `TenantRepository`, `RoleAssignmentRepository`. Read-side (`UserDirectory`, tenant memberships, menus) projects straight off DbContext. `UnitOfWork.SetTenantScopeAsync` issues `SELECT set_config('app.tenant_id', …, true)` for RLS.

## Auth / RBAC
- Password hasher verifies BOTH bcrypt (pgcrypto `crypt()` seeds, `$2a$…`, via BCrypt.Net) AND argon2id (Konscious, PHC string); new hashes are argon2id.
- JWT HMAC-SHA256, carries sub/email/jti + `tenant_id` claim. Refresh = opaque 64-byte, stored as SHA-256 hex (fits VARCHAR(64) token_hash). Rotation on refresh.
- **Resolve-once-per-request**: `PermissionResolutionMiddleware` calls `resolve_user_permissions()` ONCE → caches in request-scoped `IPermissionContext`; `[RequirePermission("key")]` checks IN MEMORY via a dynamic `perm:<key>` policy + `PermissionAuthorizationHandler`. No role names, no per-check DB calls.
- **Tenant context comes ONLY from the validated JWT `tenant_id` claim — NEVER a client header** (auditor blocker: `X-Tenant-Id`-wins let any user spoof a tenant and read another tenant's PHI via RLS `app.tenant_id`). To switch tenant: `POST /api/v1/auth/switch-tenant` validates membership server-side (`ITenantRepository.GetMembershipsAsync`, fail-closed 403 for non-members) and mints a NEW access token with the new claim, rebinding the session via `RotateRefreshWithTenantAsync`. Do NOT reintroduce any header-based tenant override.
- **Refresh rotation is a CHAIN (revoke-old + create-new session), not in-place overwrite** — so a replayed/rotated refresh token still finds its now-revoked row; reuse-after-revoke triggers `ISessionStore.RevokeAllForUserAsync` (fail-closed: logs out the whole user). `FindByRefreshHashIncludingRevokedAsync` returns rows regardless of revoked state for this detection.

## Audit
`AuditTrailWriter` does a plain parameterized INSERT into `platform.audit_log`. The DB trigger `trg_audit_chain` computes the hash chain on insert — C# must NOT compute hashes. NEVER delete audit rows; this forces soft-delete of users/tenants that have audit history (see integration test cleanup).

## DTOs added to SharedDataModel (DRY)
`Docslot/Auth/` (LoginRequest, TokenResponse, RefreshRequest, LogoutRequest, MeDto, BadgesDto) and `Docslot/Admin/` (TenantDto, UserListItemDto, CreateUserRequest, RoleDto, AssignRoleRequest, SetOverrideRequest, …). Reused existing `MenuNodeDto`, `PermissionSetDto`.

## Endpoints
`POST /api/v1/auth/{login,refresh,switch-tenant,logout}`; `GET /api/v1/me{,/permissions,/menus,/badges}`; admin: `GET /tenants`, `GET /tenants/{id}`, `GET/POST /tenants/{id}/users`, `GET /roles`, `POST /roles` (create custom role, gated `platform.roles.manage`), `POST /role-assignments` (assign, `tenant.roles.assign`), `POST /role-assignments/revoke` (soft revoke, `tenant.roles.assign`), `POST /permission-overrides` — each admin route gated by a canonical permission key. Role create/revoke added after the frontend flagged them missing; revoke is soft (revoked_at/by/reason, idempotent), never deletes. Custom roles via `Role.CreateCustom` (IsSystem always false); system roles are SQL-seeded only.

## Gotchas / decisions
- Swashbuckle is INCOMPATIBLE with ASP.NET Core 10 (TypeLoadException on GetSwagger). Use .NET 10 native `AddOpenApi()`/`MapOpenApi()`.
- Serilog `.Enrich.WithSpan()` extension wasn't resolvable; dropped it (OTel via ServiceDefaults gives trace correlation).
- Aspire AppHost references the EXTERNAL Postgres via `AddConnectionString("platform-db")` (the homebrew DB already has the canonical schema; an Aspire container would not).
- Idempotency store is in-memory for slice 01 (no idempotency table in platform_core; durable table comes with slice 03 booking/payment).
- Connection: `Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar` (homebrew trust auth, no password).
- The only build warning is transitive `MessagePack` (NU1903) pulled by the Aspire AppHost SDK — not our code.
- VERIFY WITH `dotnet build`/`dotnet test` ONLY (they terminate). NEVER `dotnet run` the Aspire AppHost to "check it boots" — it runs forever and blocks the shell. For an API smoke-test use a bounded run; NOTE macOS has no `timeout` binary, so use a background `dotnet run … &` + a watchdog `( sleep 30; kill -9 $PID ) &` and probe `/health`. ServiceDefaults only maps `/health` + `/alive` in the Development environment, so set `ASPNETCORE_ENVIRONMENT=Development` or the probes 404.
