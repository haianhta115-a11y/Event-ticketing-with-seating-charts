using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventTicketing.Api.BackgroundServices;

public class ExpiredSeatHoldCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExpiredSeatHoldCleanupService> _logger;

    public ExpiredSeatHoldCleanupService(
        IServiceProvider serviceProvider,
        ILogger<ExpiredSeatHoldCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExpiredSeatHoldCleanupService is starting.");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CleanupExpiredHoldsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ExpiredSeatHoldCleanupService is stopping.");
        }
    }

    private async Task CleanupExpiredHoldsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // If TimeProvider is registered, use it. Otherwise fallback to DateTime.UtcNow
            var timeProvider = scope.ServiceProvider.GetService<TimeProvider>();
            var now = timeProvider?.GetUtcNow().UtcDateTime ?? DateTime.UtcNow;

            var expiredHoldsCount = await dbContext.Seats
                .Where(s => s.Status == SeatStatus.Held && s.HeldUntil <= now)
                .ExecuteUpdateAsync(setter => setter
                    .SetProperty(s => s.Status, SeatStatus.Available)
                    .SetProperty(s => s.HeldByUserId, (int?)null)
                    .SetProperty(s => s.HeldUntil, (DateTime?)null),
                    stoppingToken);

            _logger.LogInformation("Cleared {Count} expired seat holds at {Time}", expiredHoldsCount, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while cleaning up expired seat holds.");
        }
    }
}
