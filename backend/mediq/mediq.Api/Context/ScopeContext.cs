using mediq.Application.Abstractions;

namespace mediq.Api.Context;

/// <summary>
/// Request-scoped holder of the client-credentials token's scopes (resolve-once, in-memory). Populated by
/// the scope-resolution middleware from the validated client JWT's <c>scope</c> claim. Kept separate from
/// <see cref="IPermissionContext"/> (user permissions) so the two auth models never bleed into each other.
/// </summary>
public sealed class ScopeContext : IScopeContext
{
    private IReadOnlySet<string> _scopes = new HashSet<string>(StringComparer.Ordinal);
    public bool IsClientToken { get; private set; }
    public IReadOnlySet<string> Scopes => _scopes;
    public Guid? ClientId { get; private set; }

    public void Set(Guid clientId, IReadOnlySet<string> scopes)
    {
        ClientId = clientId;
        _scopes = scopes;
        IsClientToken = true;
    }

    public bool Has(string scopeKey) => _scopes.Contains(scopeKey);
}
