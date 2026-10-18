using EventTicketing.Api.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Api.BackgroundServices;

/// <summary>
/// S-23 / T-52: Tiến trình chạy nền mỗi phút để quét và hủy đơn hàng quá hạn thanh toán
/// </summary>
public sealed class ExpiredOrderCleanupService(
    IServiceProvider services,
    ILogger<ExpiredOrderCleanupService> logger,
    TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExpiredOrderCleanupService is starting.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            // Drain immediately on every startup, before waiting for the first tick (T-52).
            await RunPassAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunPassAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("ExpiredOrderCleanupService is stopping.");
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<ExpiredOrderCleanup>().ExecuteAsync(ct);
            if (count > 0)
            {
                logger.LogInformation("Hủy {Count} đơn hàng quá hạn và nhả ghế tại {Time}", count, clock.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Quét đơn hàng quá hạn thất bại; sẽ thử lại ở lượt tiếp theo.");
        }
    }
}
