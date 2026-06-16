namespace mediq.Utilities.Services;

public interface ICurrentUserService
{
    string? CurrentUserId { get; }
    string? CurrentUserName { get; }
    int? CurrentRoleId { get; }
    string? CurrentUserRole { get; }
    int? CurrentCompanyId { get; }
}
