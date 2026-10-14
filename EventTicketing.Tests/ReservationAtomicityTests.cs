using System.Data.Common;
using EventTicketing.Api.Entities;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Services;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class ReservationAtomicityTests
{
    private sealed class Clock : TimeProvider, IReservationClock
    {
        public DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<DateTime> NowAsync(EventTicketing.Api.Data.AppDbContext db, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Now.UtcDateTime);
        }
    }

    private static async Task<LocalPostgresDatabase> Fixture()
    {
        var f = await LocalPostgresDatabase.CreateAsync();
        await LocalPostgresDatabase.EnsureShowtimeAsync(f.Context, 10);
        await new SeatMapImportService(f.Context, TimeProvider.System).ImportAsync(10, new()
        {
            Seats = Enumerable.Range(1, 3).Select(i => (SeatMapImportItem?)new SeatMapImportItem
            { Row = "A", Number = i.ToString(), Category = "Standard" }).ToList()
        });
        return f;
    }

    private static Task<int[]> Ids(LocalPostgresDatabase f) =>
        f.Context.Seats.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();

    [Fact]
    public async Task RepeatAndBatch_ReuseFirstExpiry_ThenExpiredSessionStartsFresh()
    {
        await using var f = await Fixture();
        var ids = await Ids(f); var clock = new Clock();
        var service = new SeatReservationService(f.Context, clock);
        var first = await service.HoldSeatAsync(10, ids[0], 101);
        clock.Now = clock.Now.AddSeconds(7);
        var repeat = await service.HoldSeatAsync(10, ids[0], 101);
        var batch = await service.HoldSeatsAsync(10, ids, 101);
        Assert.True(first.IsSuccess); Assert.True(repeat.IsSuccess); Assert.True(batch.IsSuccess);
        Assert.Equal(first.HeldUntilUtc, repeat.HeldUntilUtc);
        Assert.Equal(593, repeat.RemainingSeconds);
        Assert.Equal(3, batch.HeldSeats.Count);
        Assert.All(batch.HeldSeats, s => Assert.Equal(first.HeldUntilUtc, s.HeldUntilUtc));
        clock.Now = clock.Now.AddMinutes(11);
        var fresh = await service.HoldSeatsAsync(10, ids, 102);
        Assert.True(fresh.IsSuccess);
        Assert.All(fresh.HeldSeats, s => Assert.Equal(clock.Now.AddMinutes(10).UtcDateTime, s.HeldUntilUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchConflict_RollsBackEarlierSeat_AndPreservesPriorOperation(bool sold)
    {
        await using var f = await Fixture();
        var ids = await Ids(f); var clock = new Clock();
        var service = new SeatReservationService(f.Context, clock);
        Assert.True((await service.HoldSeatAsync(10, ids[2], 101)).IsSuccess);
        if (sold)
            await f.Context.Seats.Where(s => s.Id == ids[1]).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Sold));
        else
            Assert.True((await service.HoldSeatAsync(10, ids[1], 102)).IsSuccess);
        var failed = await service.HoldSeatsAsync(10, [ids[0], ids[1]], 101);
        Assert.False(failed.IsSuccess);
        Assert.Equal(sold ? HoldFailureReason.AlreadySold : HoldFailureReason.AlreadyHeld, failed.FailureReason);
        await using var verify = f.CreateContext();
        var rows = await verify.Seats.OrderBy(s => s.Id).ToArrayAsync();
        Assert.Equal(SeatStatus.Available, rows[0].Status); Assert.Null(rows[0].HeldByUserId); Assert.Null(rows[0].HeldUntil);
        Assert.Equal(sold ? SeatStatus.Sold : SeatStatus.Held, rows[1].Status);
        Assert.Equal(101, rows[2].HeldByUserId);
    }

    [Fact]
    public async Task OppositeOrderBatches_IndependentInstances_OnlyOneWholeBatchWins()
    {
        await using var f = await Fixture();
        var ids = await Ids(f); var barrier = new ConcurrentSeatReadBarrier(); var clock = new Clock();
        await using var a = f.CreateContext(barrier); await using var b = f.CreateContext(barrier);
        var results = await Task.WhenAll(
            new SeatReservationService(a, clock).HoldSeatsAsync(10, ids, 101),
            new SeatReservationService(b, clock).HoldSeatsAsync(10, ids.Reverse().ToArray(), 102)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(SeatReservationService.AlreadyHeldMessage, Assert.Single(results, r => !r.IsSuccess).ErrorMessage);
        var owners = await f.Context.Seats.AsNoTracking().Select(s => s.HeldByUserId).Distinct().ToArrayAsync();
        Assert.Single(owners); Assert.Contains(owners[0], new int?[] { 101, 102 });
        Assert.Equal(3, await f.Context.Seats.CountAsync(s => s.Status == SeatStatus.Held));
    }

    private sealed class AfterUpdate(Action action) : DbCommandInterceptor
    {
        private int calls;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE seats", StringComparison.Ordinal)
                && Interlocked.Increment(ref calls) == 1) action();
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterFirstUpdate_RollsBackEntireBatch_AndReleasesLocks(bool cancel)
    {
        await using var f = await Fixture(); var ids = await Ids(f); var clock = new Clock();
        using var cts = new CancellationTokenSource();
        var triggered = false;
        await using var context = f.CreateContext(new AfterUpdate(() =>
        {
            triggered = true;
            if (cancel) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("Injected failure after first persisted update");
        }));
        var task = new SeatReservationService(context, clock).HoldSeatsAsync(10, ids, 101, cts.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.True(triggered);
        Assert.Equal(3, await f.Context.Seats.CountAsync(s => s.Status == SeatStatus.Available && s.HeldUntil == null && s.HeldByUserId == null));
        var retry = await new SeatReservationService(f.Context, clock).HoldSeatsAsync(10, ids, 101).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(retry.IsSuccess);
    }

    [Fact]
    public async Task PreCancelledRequest_DoesNotAcquireAnySeat()
    {
        await using var f = await Fixture(); var ids = await Ids(f);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SeatReservationService(f.Context, new Clock()).HoldSeatsAsync(10, ids, 101, cts.Token));
        Assert.Equal(3, await f.Context.Seats.CountAsync(s => s.Status == SeatStatus.Available));
    }

    private sealed class PauseUpdate : DbCommandInterceptor
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE seats", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObserveSessionLock : DbCommandInterceptor
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task SaleCommittedAfterHoldRead_CannotBeOverwrittenByHold()
    {
        await using var f = await Fixture(); var ids = await Ids(f); var pause = new PauseUpdate();
        await using var context = f.CreateContext(pause);
        var hold = new SeatReservationService(context, new Clock()).HoldSeatAsync(10, ids[0], 101);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(1, await f.Context.Seats.Where(s => s.Id == ids[0] && s.Status == SeatStatus.Available)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Sold)));
        }
        finally { pause.Continue.TrySetResult(); }
        var result = await hold;
        Assert.False(result.IsSuccess); Assert.Equal(HoldFailureReason.AlreadySold, result.FailureReason);
        var seat = await f.Context.Seats.AsNoTracking().SingleAsync(s => s.Id == ids[0]);
        Assert.Equal(SeatStatus.Sold, seat.Status); Assert.Null(seat.HeldByUserId);
    }

    [Fact]
    public async Task SameUserDifferentInstances_ShareExpiryEvenWhenRequestsOverlap()
    {
        await using var f = await Fixture(); var ids = await Ids(f); var pause = new PauseUpdate();
        var firstClock = new Clock(); var secondClock = new Clock { Now = firstClock.Now.AddSeconds(7) };
        var waiting = new ObserveSessionLock();
        await using var a = f.CreateContext(pause); await using var b = f.CreateContext(waiting);
        var first = new SeatReservationService(a, firstClock).HoldSeatAsync(10, ids[0], 101);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = new SeatReservationService(b, secondClock).HoldSeatAsync(10, ids[1], 101);
        await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(second.IsCompleted);
        pause.Continue.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(results[0].HeldUntilUtc, results[1].HeldUntilUtc);
        Assert.Equal(593, results[1].RemainingSeconds);
    }

    [Fact]
    public async Task CancellationWhileWaitingForSessionLock_DoesNotWriteOrLeakLock()
    {
        await using var f = await Fixture(); var ids = await Ids(f); var pause = new PauseUpdate();
        var waiting = new ObserveSessionLock(); var clock = new Clock();
        await using var a = f.CreateContext(pause); await using var b = f.CreateContext(waiting);
        using var cts = new CancellationTokenSource();
        var first = new SeatReservationService(a, clock).HoldSeatAsync(10, ids[0], 101);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = new SeatReservationService(b, clock).HoldSeatAsync(10, ids[1], 101, cts.Token);
            await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(second.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Continue.TrySetResult(); }
        Assert.True((await first).IsSuccess);
        Assert.Equal(SeatStatus.Available, (await f.Context.Seats.AsNoTracking().SingleAsync(s => s.Id == ids[1])).Status);
        Assert.True((await new SeatReservationService(f.Context, clock).HoldSeatAsync(10, ids[1], 101)
            .WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
    }
}
