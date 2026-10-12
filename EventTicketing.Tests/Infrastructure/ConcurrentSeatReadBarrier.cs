using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EventTicketing.Tests.Infrastructure;

// Force both independent requests to read the seat before either can mutate it.
internal sealed class ConcurrentSeatReadBarrier : DbCommandInterceptor
{
    private int arrivals;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM seats AS", StringComparison.Ordinal)
            && command.CommandText.Contains("LIMIT 1", StringComparison.Ordinal)
            && Interlocked.Increment(ref arrivals) <= 2)
        {
            if (Volatile.Read(ref arrivals) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}
