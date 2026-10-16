using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Api.Configuration;

public static class PaymentGatewayConfiguration
{
    public const string MockProvider = "Mock";
    public const string SandboxProvider = "Sandbox";

    public static PaymentOptions ConfigurePaymentGateway(
        IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var paymentOptions = configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions();

        var envGateway = Environment.GetEnvironmentVariable("PAYMENT_GATEWAY")
            ?? Environment.GetEnvironmentVariable("PAYMENT_PROVIDER");

        if (!string.IsNullOrWhiteSpace(envGateway))
        {
            paymentOptions.Provider = envGateway.Trim();
        }

        // T-44 (SCRUM-114): Giả sử môi trường là production, Khi khởi động với cổng giả lập, Thì ứng dụng từ chối khởi động
        if (environment.IsProduction() &&
            string.Equals(paymentOptions.Provider, MockProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Không thể khởi động ứng dụng ở môi trường Production với cổng thanh toán giả lập (Mock). Vui lòng cấu hình cổng thanh toán chính thức.");
        }

        services.Configure<PaymentOptions>(options =>
        {
            options.Provider = paymentOptions.Provider;
            options.MockBaseUrl = paymentOptions.MockBaseUrl;
            options.SandboxBaseUrl = paymentOptions.SandboxBaseUrl;
            options.ReturnUrl = paymentOptions.ReturnUrl;
            options.WebhookSecret = paymentOptions.WebhookSecret;
        });

        if (string.Equals(paymentOptions.Provider, MockProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPaymentGateway, MockPaymentGateway>();
        }
        else
        {
            services.AddSingleton<IPaymentGateway, SandboxPaymentGateway>();
        }

        return paymentOptions;
    }
}
