using System.Net;
using mediq.Utilities.Exceptions;

namespace mediq.Infrastructure.Ai;

/// <summary>
/// Maps an AI-sibling-service 4xx response onto the platform's typed exceptions so the gate decision the AI
/// service made (authorization / validation / not-found) is surfaced as the SAME HTTP status to the original
/// caller — never silently collapsed into an "unavailable" result. 5xx / other statuses are NOT mapped (the
/// adapter returns null → the caller renders "unavailable"). Reusable across the OCR + RAG PHI proxies.
///
/// REUSABLE LESSON (extends the triage purpose-of-use mirror): a .NET proxy to a PHI-gated AI endpoint must
/// either mirror the AI gate before the call OR propagate the AI's 4xx — else a consent/RBAC denial raced
/// after the .NET pre-check is masked as Available=false and the security decision is invisible to the caller.
/// </summary>
internal static class AiErrorMapper
{
    public static void ThrowIfClientError(HttpStatusCode status) => _ = (int)status switch
    {
        401 or 403 => throw new ForbiddenException("The AI service refused this request (authorization)."),
        400 or 422 => throw new ValidationException(
            new Dictionary<string, string[]> { ["ai"] = ["The AI service rejected this request (validation)."] }),
        404 => throw new KeyNotFoundException("The AI service could not find the requested resource."),
        _ => 0,   // 5xx and any other non-success → not a client decision; caller treats as unavailable
    };
}
