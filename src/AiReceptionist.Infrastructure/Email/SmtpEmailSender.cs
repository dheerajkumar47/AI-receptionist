using AiReceptionist.Core.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AiReceptionist.Infrastructure.Email;

/// <summary>
/// SMTP sender (MailKit) used as the booking fallback (or a pickup folder of .eml files in development). Deliberately independent of Microsoft Graph so it
/// still works when the calendar API is down or unauthorised.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;

    public SmtpEmailSender(IOptions<SmtpOptions> options) => _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;
    public string? OwnerAddress => _options.OwnerAddress;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (message.To.Count == 0) throw new InvalidOperationException("No recipients.");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress!));
        foreach (var to in message.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder { TextBody = message.Body };
        foreach (var a in message.Attachments ?? Array.Empty<EmailAttachment>())
            builder.Attachments.Add(a.FileName, a.Data, ContentType.Parse(a.ContentType));
        mime.Body = builder.ToMessageBody();

        var security = _options.Security.ToLowerInvariant() switch
        {
            "sslonconnect" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            Directory.CreateDirectory(_options.PickupDirectory!);
            var path = Path.Combine(_options.PickupDirectory!, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");
            await mime.WriteToAsync(path, ct);
            return;
        }

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host!, _options.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
