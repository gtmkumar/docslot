namespace mediq.Utilities.HealthChecks;

public class HealthCheckResponse
{
    public string Status { get; set; } = string.Empty;
    public IEnumerable<HealthCheck> Checks { get; set; } = Array.Empty<HealthCheck>();
    public TimeSpan Duration { get; set; }
}
