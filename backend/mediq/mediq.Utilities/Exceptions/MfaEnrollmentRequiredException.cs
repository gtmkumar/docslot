namespace mediq.Utilities.Exceptions;

/// <summary>
/// Thrown at login when the tenant's security policy requires the authenticating user's role tier to have
/// two-factor enabled, but the account has not enrolled a second factor yet. This is a DISTINCT outcome from
/// bad-credentials (401) and account-locked (403 generic): the credentials were correct, but the session is
/// deliberately withheld until the user enrols. Maps to HTTP 403 with the machine-readable message
/// <c>mfa_enrollment_required</c>, which the client keys off to route into the enrolment flow.
/// </summary>
public sealed class MfaEnrollmentRequiredException : AppExceptionBase
{
    /// <summary>The stable code the frontend branches on (carried as the exception message + response message).</summary>
    public const string Code = "mfa_enrollment_required";

    public MfaEnrollmentRequiredException() : base("MFA enrolment required", Code) { }
}
