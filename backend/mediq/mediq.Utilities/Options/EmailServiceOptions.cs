namespace mediq.Utilities.Options;

public class EmailServiceOptions
{
    public string HostAddress { get; set; } = string.Empty;
    public int HostPort { get; set; }
    public string HostUsername { get; set; } = string.Empty;
    public string HostPassword { get; set; } = string.Empty;
    public SecureSocketOptions HostSecureSocketOptions { get; set; } = SecureSocketOptions.Auto;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string TemplateSourceDirectory { get; set; } = string.Empty;
}
