using EventTicketing.Api.Services;

namespace EventTicketing.Api.BackgroundServices;

public sealed class ExpiredSeatHoldCleanupService(IServiceProvider services,
    ILogger<ExpiredSeatHoldCleanupService> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExpiredSeatHoldCleanupService is starting.");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            // Drain immediately on every startup, before waiting for the first tick.
            await RunPassAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RunPassAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("ExpiredSeatHoldCleanupService is stopping.");
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<ExpiredSeatHoldCleanup>().ExecuteAsync(ct);
            logger.LogInformation("Cleared {Count} expired seat holds at {Time}", count, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogError("Expired hold cleanup failed; retrying at the next scheduled pass.");
        }
    }
}
