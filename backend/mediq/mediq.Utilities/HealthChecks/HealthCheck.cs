namespace mediq.Utilities.HealthChecks;

public abstract class HealthCheck
{
    public string Status { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
