using EventTicketing.Api.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using EventTicketing.Tests.Infrastructure;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class EmailDeliveryTests
{
    private static IConfiguration Configuration(string mode, string url) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Mode"] = mode, ["Email:ActivationUrl"] = url }).Build();

    [Fact]
    public void Testing_CannotSelectExternalSmtpDelivery()
    {
        var services = new ServiceCollection();
        services.AddActivationEmail(Configuration("Smtp", "http://localhost:5173/activate"), new EnvironmentStub("Testing"));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<DisabledEmailDelivery>(provider.GetRequiredService<IEmailDelivery>());
    }

    [Fact]
    public void DisabledEmail_PreservesExistingDeploymentStartup()
    {
        var services = new ServiceCollection();
        services.AddActivationEmail(Configuration("Disabled", ""), new EnvironmentStub("Production"));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<DisabledEmailDelivery>(provider.GetRequiredService<IEmailDelivery>());
    }

    [Theory]
    [InlineData("Pickup", "https://example.test/activate", "Staging")]
    [InlineData("Smtp", "http://example.test/activate", "Production")]
    [InlineData("Pickup", "http://example.test/activate", "Development")]
    [InlineData("Smtp", "https://example.test/activate", "Production")]
    public void UnsafeOrIncompleteEmailConfiguration_IsRejected(string mode, string url, string environment)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddActivationEmail(Configuration(mode, url), new EnvironmentStub(environment)));
    }

    [Fact]
    public async Task DevelopmentPickup_WritesOnlyLocalMailbox_NoNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "event-ticketing-email-test-" + Guid.NewGuid().ToString("N"));
        var sender = new PickupEmailDelivery(new EnvironmentStub("Development") { ContentRootPath = root });
        await sender.SendAsync(new EmailMessage("fixture@example.test", "Fixture", "Local fake activation message"), CancellationToken.None);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(root, ".local-mail")));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("Local fake activation message", content);
        // Retain this newly created temporary mailbox; never clean unrelated data.
    }

    [Fact]
    public async Task SmtpQueue_ReturnsWithoutWaitingForTransport()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sender = new QueuedEmailDelivery(new DelegateDelivery(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }), NullLogger<QueuedEmailDelivery>.Instance);
        await sender.StartAsync(CancellationToken.None);
        await sender.SendAsync(new EmailMessage("fixture@example.test", "Fixture", "Fake link"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SmtpQueue_DoesNotLogTransportExceptionDetailsOrPrivateMessage()
    {
        using var logs = new CapturedLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sender = new QueuedEmailDelivery(new DelegateDelivery((_, _) =>
        {
            failed.TrySetResult();
            throw new InvalidOperationException("private-provider-detail");
        }), factory.CreateLogger<QueuedEmailDelivery>());
        await sender.StartAsync(CancellationToken.None);
        await sender.SendAsync(new EmailMessage("fixture@example.test", "Fixture", "private-message"), CancellationToken.None);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.StopAsync(CancellationToken.None);
        Assert.Contains(logs.Messages, message => message.Contains("Activation email delivery failed"));
        Assert.All(logs.Messages, message =>
        {
            var leaked = message.Contains("private-provider-detail") || message.Contains("private-message");
            Assert.False(leaked);
        });
    }

    private sealed class DelegateDelivery(Func<EmailMessage, CancellationToken, Task> send) : IEmailDelivery
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) => send(message, cancellationToken);
    }

    private sealed class EnvironmentStub(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "EmailTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
