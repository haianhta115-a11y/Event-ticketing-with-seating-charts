namespace EventTicketing.Api.Email;

public static class EmailSetup
{
    public static void AddActivationEmail(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var options = configuration.GetSection("Email").Get<EmailDeliveryOptions>() ?? new();
        if ((options.Mode != "Disabled" || !string.IsNullOrWhiteSpace(options.ActivationUrl)) &&
            (!Uri.TryCreate(options.ActivationUrl, UriKind.Absolute, out var url) ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
            (url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback &&
                (environment.IsDevelopment() || environment.IsEnvironment("Testing"))))))
            throw new InvalidOperationException("Configure Email__ActivationUrl with an absolute HTTPS activation page URL (loopback HTTP is allowed locally).");
        services.AddSingleton(options);
        if (environment.IsEnvironment("Testing"))
            services.AddSingleton<IEmailDelivery, DisabledEmailDelivery>(); // Tests must explicitly inject a fake.
        else if (options.Mode == "Pickup" && environment.IsDevelopment())
            services.AddSingleton<IEmailDelivery, PickupEmailDelivery>();
        else if (options.Mode == "Smtp")
        {
            if (string.IsNullOrWhiteSpace(options.Host) || options.Port < 1 || options.Port > 65535 ||
                string.IsNullOrWhiteSpace(options.From) || string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
                throw new InvalidOperationException("Configure SMTP through Email__Host/Port/From/Username/Password environment variables.");
            services.AddSingleton<SmtpEmailDelivery>();
            services.AddSingleton(provider => new QueuedEmailDelivery(provider.GetRequiredService<SmtpEmailDelivery>(),
                provider.GetRequiredService<ILogger<QueuedEmailDelivery>>()));
            services.AddSingleton<IEmailDelivery>(provider => provider.GetRequiredService<QueuedEmailDelivery>());
            services.AddHostedService(provider => provider.GetRequiredService<QueuedEmailDelivery>());
        }
        else if (options.Mode == "Disabled") services.AddSingleton<IEmailDelivery, DisabledEmailDelivery>();
        else throw new InvalidOperationException("Unsupported email delivery mode for this environment.");
    }
}
