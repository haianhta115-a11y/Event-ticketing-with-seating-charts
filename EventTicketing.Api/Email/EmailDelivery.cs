using System.Net;
using System.Net.Mail;
using System.Text.Json;

namespace EventTicketing.Api.Email;

public sealed record EmailMessage(string Recipient, string Subject, string Body);
public interface IEmailDelivery
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailDeliveryOptions
{
    public string Mode { get; set; } = "Disabled";
    public string ActivationUrl { get; set; } = "";
    public string PickupDirectory { get; set; } = ".local-mail";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string From { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SmtpEmailDelivery(EmailDeliveryOptions options) : IEmailDelivery
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(options.Username, options.Password)
        };
        using var mail = new MailMessage(options.From, message.Recipient, message.Subject, message.Body);
        await client.SendMailAsync(mail, cancellationToken);
    }
}

// Development-only mailbox: no external email and no tokens in logs.
public sealed class PickupEmailDelivery(IWebHostEnvironment environment, EmailDeliveryOptions? options = null) : IEmailDelivery
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Pickup email is Development-only.");
        var directory = Path.GetFullPath(options?.PickupDirectory ?? ".local-mail", environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(message), cancellationToken);
    }
}

public sealed class DisabledEmailDelivery : IEmailDelivery
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Email delivery is disabled.");
}
