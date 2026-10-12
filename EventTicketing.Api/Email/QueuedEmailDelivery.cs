using System.Threading.Channels;

namespace EventTicketing.Api.Email;

// Keep SMTP network latency out of the registration response (account enumeration).
// This bounded in-process queue is best effort; interrupted delivery is recoverable via resend.
public sealed class QueuedEmailDelivery(IEmailDelivery transport, ILogger<QueuedEmailDelivery> logger)
    : BackgroundService, IEmailDelivery
{
    private readonly Channel<EmailMessage> messages = Channel.CreateBounded<EmailMessage>(1000);

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!messages.Writer.TryWrite(message)) throw new InvalidOperationException("Email queue is full.");
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in messages.Reader.ReadAllAsync(stoppingToken))
        {
            try { await transport.SendAsync(message, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Activation email delivery failed; account remains inactive and resend is available.");
            }
        }
    }
}
