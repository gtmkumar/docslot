using mediq.Utilities.Options;

namespace mediq.Utilities.Services;

public interface IEmailService
{
    Task SendEmailAsync(
        string email,
        string subject,
        string[] messageBody,
        string? templateName,
        CancellationToken cancellationToken = default);

    Task SendEmailWithAttachmentAsync(
        string email,
        string subject,
        string[] messageBody,
        string? templateName,
        IFormFileCollection? attachments = null,
        CancellationToken cancellationToken = default);
}

public class EmailService : IEmailService
{
    private readonly IWebHostEnvironment _environment;
    private readonly EmailServiceOptions _options;

    public EmailService(IOptions<EmailServiceOptions> options, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public Task SendEmailAsync(
        string email,
        string subject,
        string[] messageBody,
        string? templateName,
        CancellationToken cancellationToken = default)
        => SendAsync(email, subject, messageBody, templateName, attachments: null, cancellationToken);

    public Task SendEmailWithAttachmentAsync(
        string email,
        string subject,
        string[] messageBody,
        string? templateName,
        IFormFileCollection? attachments = null,
        CancellationToken cancellationToken = default)
        => SendAsync(email, subject, messageBody, templateName, attachments, cancellationToken);

    private async Task SendAsync(
        string to,
        string subject,
        string[] messageBody,
        string? templateName,
        IFormFileCollection? attachments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);
        ArgumentNullException.ThrowIfNull(messageBody);

        var body = await BuildBodyAsync(messageBody, templateName, cancellationToken);
        var message = await BuildMessageAsync(to, subject, body, attachments, cancellationToken);

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_options.HostAddress, _options.HostPort, _options.HostSecureSocketOptions, cancellationToken);

        if (!string.IsNullOrEmpty(_options.HostUsername))
            await smtp.AuthenticateAsync(_options.HostUsername, _options.HostPassword, cancellationToken);

        await smtp.SendAsync(message, cancellationToken);
        await smtp.DisconnectAsync(quit: true, cancellationToken);
    }

    private async Task<string> BuildBodyAsync(string[] messageBody, string? templateName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(templateName))
            return string.Join(" ", messageBody);

        var template = await ReadTemplateAsync(templateName, cancellationToken);
        return string.IsNullOrEmpty(template)
            ? string.Join(" ", messageBody)
            : string.Format(template, messageBody);
    }

    private async Task<string> ReadTemplateAsync(string templateName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_environment.WebRootPath, _options.TemplateSourceDirectory, $"{templateName}.html");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : string.Empty;
    }

    private async Task<MimeMessage> BuildMessageAsync(
        string to,
        string subject,
        string body,
        IFormFileCollection? attachments,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage
        {
            Sender = MailboxAddress.Parse(_options.SenderEmail),
            Subject = subject
        };

        if (!string.IsNullOrEmpty(_options.SenderName))
            message.Sender.Name = _options.SenderName;

        message.From.Add(message.Sender);
        message.To.Add(MailboxAddress.Parse(to));

        var bodyBuilder = new BodyBuilder { HtmlBody = body };

        if (attachments is { Count: > 0 })
        {
            foreach (var file in attachments)
            {
                if (file.Length <= 0) continue;

                await using var stream = file.OpenReadStream();
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);

                bodyBuilder.Attachments.Add(
                    file.FileName,
                    memory.ToArray(),
                    ContentType.Parse(file.ContentType));
            }
        }

        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }
}
