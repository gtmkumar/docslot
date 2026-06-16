using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace mediq.Api.Authorization;

/// <summary>
/// Declarative permission gate, e.g. <c>[RequirePermission("platform.tenants.read")]</c>. Backed by an
/// <see cref="IAuthorizationRequirement"/> whose handler checks the resolve-once
/// <see cref="IPermissionContext"/> IN MEMORY. No role names, ever.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(string permissionKey) => Policy = PolicyPrefix + permissionKey;
}

public sealed class PermissionRequirement(string permissionKey) : IAuthorizationRequirement
{
    public string PermissionKey { get; } = permissionKey;
}

public sealed class PermissionAuthorizationHandler(IPermissionContext permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // In-memory check against the per-request resolved set (deny-wins already applied by the SQL resolver).
        if (permissions.Has(requirement.PermissionKey))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Materializes <c>perm:&lt;key&gt;</c> (user-permission) and <c>scope:&lt;key&gt;</c> (client-scope) policies
/// on demand so we don't pre-register every permission/scope. Permission policies require an authenticated
/// user; scope policies require any authenticated principal (the client token carries no user).
/// </summary>
public sealed class PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    : Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider
{
    private readonly Microsoft.AspNetCore.Authorization.DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var key = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(key))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        if (policyName.StartsWith(RequireScopeAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var key = policyName[RequireScopeAttribute.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ScopeRequirement(key))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
