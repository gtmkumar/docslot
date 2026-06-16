using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace mediq.Api.Authorization;

/// <summary>
/// Declarative API-scope gate for client-credentials endpoints, e.g.
/// <c>[RequireScope("docslot.bookings.read")]</c>. Distinct from <see cref="RequirePermissionAttribute"/>:
/// scopes come from a CLIENT token's <c>scope</c> claim (resolve-once into <see cref="IScopeContext"/>),
/// permissions come from a USER token. The two schemes are separate but compose cleanly at the API.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireScopeAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "scope:";
    public RequireScopeAttribute(string scopeKey) => Policy = PolicyPrefix + scopeKey;
}

public sealed class ScopeRequirement(string scopeKey) : IAuthorizationRequirement
{
    public string ScopeKey { get; } = scopeKey;
}

public sealed class ScopeAuthorizationHandler(IScopeContext scopes) : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        // In-memory check against the resolve-once client scope set. Only client tokens carry scopes.
        if (scopes.IsClientToken && scopes.Has(requirement.ScopeKey))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
