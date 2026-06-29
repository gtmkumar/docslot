namespace mediq.Application.Options;

/// <summary>Selects + configures the AI sibling-service client. Dev/test default to a deterministic stub (the AI
/// service need not be running); prod sets 'http' with the AI service's base URL. The caller's bearer JWT is
/// forwarded to the AI service (it validates the same DocSlot JWT).</summary>
public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    /// <summary>'stub' (dev/test default — deterministic local heuristic) | 'http' (call the real AI service).</summary>
    public string Provider { get; set; } = "stub";

    /// <summary>Base URL of the AI service (used only by the 'http' provider).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Per-call timeout for the 'http' provider (seconds) — kept short so a slow AI service never blocks the read.</summary>
    public int TimeoutSeconds { get; set; } = 5;
}
