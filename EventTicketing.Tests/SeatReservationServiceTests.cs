using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Tests;

public sealed class SeatReservationServiceTests
{
    private static readonly DateTimeOffset BaselineTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // =========================================================================
    // AC 1: Giả sử ghế đang trống: Khi bấm chọn -> Chuyển "Held", đồng hồ đếm ngược 10 phút
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_WhenSeatIsAvailable_HoldsSeatAndStarts10MinuteCountdown()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "1", SeatStatus.Available);

        var result = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        Assert.True(result.IsSuccess);
        Assert.Equal(seat.Id, result.SeatId);
        Assert.Equal(SeatStatus.Held, result.Status);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10).UtcDateTime, result.HeldUntilUtc);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, result.ServerTimeUtc);
        Assert.Equal(600, result.RemainingSeconds); // 10 minutes = 600s
        Assert.Single(result.HeldSeats);
        Assert.Equal("A", result.HeldSeats[0].Row);
        Assert.Equal("1", result.HeldSeats[0].Number);

        // Verify in DB
        database.Context.ChangeTracker.Clear();
        var dbSeat = await database.Context.Seats.FindAsync(seat.Id);
        Assert.NotNull(dbSeat);
        Assert.Equal(SeatStatus.Held, dbSeat.Status);
        Assert.Equal(101, dbSeat.HeldByUserId);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10).UtcDateTime, dbSeat.HeldUntil);
    }

    // =========================================================================
    // AC 2: Giả sử ghế đang có người khác giữ: Bấm chọn -> Từ chối "ghế vừa có người chọn"
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_WhenSeatIsHeldByAnotherUser_RejectsWithAlreadyHeldMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "2", SeatStatus.Available);

        // User A giữ trước
        var userAResult = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);
        Assert.True(userAResult.IsSuccess);

        // User B thử giữ ghế này
        var userBResult = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 102);

        Assert.False(userBResult.IsSuccess);
        Assert.Equal(HoldFailureReason.AlreadyHeld, userBResult.FailureReason);
        Assert.Equal("ghế vừa có người chọn", userBResult.ErrorMessage);
        Assert.NotNull(userBResult.CurrentSeat);
        Assert.Equal(SeatStatus.Held, userBResult.CurrentSeat.Status);

        // Ghế vẫn thuộc về User A trong DB
        database.Context.ChangeTracker.Clear();
        var dbSeat = await database.Context.Seats.FindAsync(seat.Id);
        Assert.NotNull(dbSeat);
        Assert.Equal(101, dbSeat.HeldByUserId);
    }

    [Fact]
    public async Task HoldSeatAsync_WhenSeatHoldHasExpired_AllowsAnotherUserToHoldSeat()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "3", SeatStatus.Available);

        // User A giữ ghế tại T = 0
        await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        // Trôi qua 10 phút 1 giây -> Phiên giữ chỗ của User A hết hạn
        clock.Advance(TimeSpan.FromSeconds(601));

        // User B chọn ghế này
        var userBResult = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 102);

        Assert.True(userBResult.IsSuccess);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(102, (await database.Context.Seats.FindAsync(seat.Id))?.HeldByUserId);
        Assert.Equal(600, userBResult.RemainingSeconds);
    }

    [Fact]
    public async Task HoldSeatAsync_WhenSeatIsAlreadySold_RejectsWithAlreadySold()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "4", SeatStatus.Sold);

        var result = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        Assert.False(result.IsSuccess);
        Assert.Equal(HoldFailureReason.AlreadySold, result.FailureReason);
        Assert.Equal("Ghế đã được bán.", result.ErrorMessage);
    }

    // =========================================================================
    // AC 3: Chọn nhiều ghế cách nhau vài giây: Dùng chung MỘT thời hạn tính từ ghế đầu tiên
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_WhenSelectingMultipleSeats_AllShareTheExactSameExpiryFromFirstSeat()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat1 = await CreateSeatAsync(database.Context, performanceId: 1, row: "B", number: "1", SeatStatus.Available);
        var seat2 = await CreateSeatAsync(database.Context, performanceId: 1, row: "B", number: "2", SeatStatus.Available);
        var seat3 = await CreateSeatAsync(database.Context, performanceId: 1, row: "B", number: "3", SeatStatus.Available);

        const int userId = 200;

        // T = 0: Chọn ghế 1
        var result1 = await service.HoldSeatAsync(performanceId: 1, seatId: seat1.Id, userId: userId);
        Assert.True(result1.IsSuccess);
        var initialExpiry = result1.HeldUntilUtc;
        Assert.Equal(600, result1.RemainingSeconds);

        // T = +5 giây: Chọn ghế 2
        clock.Advance(TimeSpan.FromSeconds(5));
        var result2 = await service.HoldSeatAsync(performanceId: 1, seatId: seat2.Id, userId: userId);
        Assert.True(result2.IsSuccess);

        // T = +10 giây tiếp (tổng +15s): Chọn ghế 3
        clock.Advance(TimeSpan.FromSeconds(10));
        var result3 = await service.HoldSeatAsync(performanceId: 1, seatId: seat3.Id, userId: userId);
        Assert.True(result3.IsSuccess);

        // Kiểm tra: Cả 3 ghế PHẢI CÙNG 1 mốc thời hạn HeldUntilUtc duy nhất!
        Assert.Equal(initialExpiry, result2.HeldUntilUtc);
        Assert.Equal(initialExpiry, result3.HeldUntilUtc);

        // Kiểm tra: Thời gian còn lại giảm dần theo đồng hồ máy chủ từ ghế đầu tiên
        Assert.Equal(595, result2.RemainingSeconds); // 600 - 5s
        Assert.Equal(585, result3.RemainingSeconds); // 600 - 15s

        // Danh sách ghế đang giữ của phiên chứa đủ 3 ghế
        Assert.Equal(3, result3.HeldSeats.Count);

        // Kiểm tra trong DB: Cả 3 ghế đều có HeldUntil giống nhau
        database.Context.ChangeTracker.Clear();
        var dbSeats = await database.Context.Seats
            .Where(s => s.PerformanceId == 1 && s.HeldByUserId == userId)
            .ToListAsync();
        Assert.Equal(3, dbSeats.Count);
        Assert.All(dbSeats, s => Assert.Equal(initialExpiry, s.HeldUntil));
    }

    // =========================================================================
    // AC 4: Giả sử suất diễn đã đóng bán: Từ chối với thông báo "suất không còn mở bán"
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_WhenPerformanceSalesIsClosed_RejectsWithSalesClosedMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        // Tạo suất diễn đã đóng bán (IsSalesClosed = true)
        var performance = new Performance
        {
            Id = 99,
            Title = "Đêm nhạc đặc biệt",
            StartTime = BaselineTime.AddHours(2).UtcDateTime,
            IsSalesClosed = true
        };
        database.Context.Performances.Add(performance);
        await database.Context.SaveChangesAsync();

        var seat = await CreateSeatAsync(database.Context, performanceId: 99, row: "C", number: "1", SeatStatus.Available);

        var result = await service.HoldSeatAsync(performanceId: 99, seatId: seat.Id, userId: 101);

        Assert.False(result.IsSuccess);
        Assert.Equal(HoldFailureReason.SalesClosed, result.FailureReason);
        Assert.Equal("suất không còn mở bán", result.ErrorMessage);

        // Ghế vẫn ở trạng thái Available, không bị chiếm
        var dbSeat = await database.Context.Seats.FindAsync(seat.Id);
        Assert.NotNull(dbSeat);
        Assert.Equal(SeatStatus.Available, dbSeat.Status);
        Assert.Null(dbSeat.HeldByUserId);
    }

    // =========================================================================
    // AC 5: Server-authoritative timestamp: Chống lệch giờ 5 phút của máy client
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_ServerAuthoritative_ProvidesExactServerUtcAndRemainingSeconds()
    {
        await using var database = await TestDatabase.CreateAsync();
        var serverNow = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(serverNow);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "D", number: "1", SeatStatus.Available);

        var result = await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        Assert.True(result.IsSuccess);
        Assert.Equal(serverNow.UtcDateTime, result.ServerTimeUtc);
        Assert.Equal(serverNow.AddMinutes(10).UtcDateTime, result.HeldUntilUtc);
        Assert.Equal(600, result.RemainingSeconds);

        // Giả sử đồng hồ Client chạy nhanh hơn Server 5 phút (14:05:00):
        var clientClockFast = serverNow.AddMinutes(5).UtcDateTime;
        // Nếu Client tự tính theo Date.now(): (14:10:00 - 14:05:00) = chỉ còn 5 phút (sai lệch!)
        var naiveClientRemaining = (result.HeldUntilUtc!.Value - clientClockFast).TotalSeconds;
        Assert.Equal(300, naiveClientRemaining); // Chứng minh Client tính sai nếu không calibrate

        // Cách chuẩn (AC 5): Client calibrate bằng ServerTimeUtc hoặc dùng RemainingSeconds của Server:
        var clientOffset = result.ServerTimeUtc - clientClockFast; // -5 phút
        var calibratedClientTime = clientClockFast + clientOffset; // 14:00:00 (chuẩn server!)
        var correctClientRemaining = (result.HeldUntilUtc!.Value - calibratedClientTime).TotalSeconds;

        Assert.Equal(600, correctClientRemaining); // Chính xác 10 phút!
        Assert.Equal(result.RemainingSeconds, (int)correctClientRemaining);
    }

    // =========================================================================
    // Concurrency / Race Condition: 2 người cùng bấm 1 ghế tại cùng thời điểm
    // =========================================================================
    [Fact]
    public async Task HoldSeatAsync_ConcurrentRequests_OnlyOneSucceedsAndTheOtherGetsAlreadyHeld()
    {
        var connection = new SqliteConnection("Data Source=shared_concurrency_test;Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using (var initDb = new AppDbContext(options))
            {
                await initDb.Database.EnsureCreatedAsync();
            }

            int seatId;
            await using (var setupDb = new AppDbContext(options))
            {
                var seat = await CreateSeatAsync(setupDb, performanceId: 5, row: "VIP", number: "99", SeatStatus.Available);
                seatId = seat.Id;
            }

            var clock = new TestTimeProvider(BaselineTime);

            // Hai context độc lập đại diện cho hai HTTP Request đồng thời từ 2 người dùng
            await using var dbUser1 = new AppDbContext(options);
            await using var dbUser2 = new AppDbContext(options);

            var serviceUser1 = new SeatReservationService(dbUser1, clock);
            var serviceUser2 = new SeatReservationService(dbUser2, clock);

            // Bấm chọn đồng thời (Race condition)
            var task1 = serviceUser1.HoldSeatAsync(performanceId: 5, seatId: seatId, userId: 101);
            var task2 = serviceUser2.HoldSeatAsync(performanceId: 5, seatId: seatId, userId: 102);

            var results = await Task.WhenAll(task1, task2);

            var successCount = results.Count(r => r.IsSuccess);
            var failureCount = results.Count(r => !r.IsSuccess && r.ErrorMessage == "ghế vừa có người chọn");

            // Chỉ duy nhất 1 người thành công, người kia bị từ chối
            Assert.Equal(1, successCount);
            Assert.Equal(1, failureCount);

            // Kiểm tra trong database: ghế chỉ được giữ bởi đúng 1 người
            await using var verifyDb = new AppDbContext(options);
            var finalSeat = await verifyDb.Seats.FindAsync(seatId);
            Assert.NotNull(finalSeat);
            Assert.Equal(SeatStatus.Held, finalSeat.Status);
            Assert.True(finalSeat.HeldByUserId == 101 || finalSeat.HeldByUserId == 102);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    // =========================================================================
    // Release Hold: Hủy giữ chỗ ghế
    // =========================================================================
    [Fact]
    public async Task ReleaseSeatAsync_WhenCalledByHoldOwner_FreesTheSeat()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "10", SeatStatus.Available);

        await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        var released = await service.ReleaseSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);
        Assert.True(released.IsSuccess);

        database.Context.ChangeTracker.Clear();
        var dbSeat = await database.Context.Seats.FindAsync(seat.Id);
        Assert.NotNull(dbSeat);
        Assert.Equal(SeatStatus.Available, dbSeat.Status);
        Assert.Null(dbSeat.HeldByUserId);
        Assert.Null(dbSeat.HeldUntil);
    }

    [Fact]
    public async Task ReleaseSeatAsync_AC1_ReleaseOneSeatKeepsOthersIntact()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat1 = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "1", SeatStatus.Available);
        var seat2 = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "2", SeatStatus.Available);

        var hold1 = await service.HoldSeatAsync(performanceId: 1, seatId: seat1.Id, userId: 101);
        var hold2 = await service.HoldSeatAsync(performanceId: 1, seatId: seat2.Id, userId: 101);

        var released = await service.ReleaseSeatAsync(performanceId: 1, seatId: seat1.Id, userId: 101);
        Assert.True(released.IsSuccess);

        database.Context.ChangeTracker.Clear();
        var dbSeat1 = await database.Context.Seats.FindAsync(seat1.Id);
        var dbSeat2 = await database.Context.Seats.FindAsync(seat2.Id);

        Assert.Equal(SeatStatus.Available, dbSeat1!.Status);
        Assert.Null(dbSeat1.HeldByUserId);
        Assert.Null(dbSeat1.HeldUntil);

        Assert.Equal(SeatStatus.Held, dbSeat2!.Status);
        Assert.Equal(101, dbSeat2.HeldByUserId);
        Assert.Equal(hold1.HeldUntilUtc, dbSeat2.HeldUntil); // Cùng chung expiration
    }

    [Fact]
    public async Task ReleaseSeatAsync_AC2_ReleaseAllSeatsRemovesHoldSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "1", SeatStatus.Available);
        await service.HoldSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);

        var released = await service.ReleaseSeatAsync(performanceId: 1, seatId: seat.Id, userId: 101);
        Assert.True(released.IsSuccess);

        var session = await service.GetUserHoldSessionAsync(performanceId: 1, userId: 101);
        Assert.Null(session);
    }

    // =========================================================================
    // Sơ đồ ghế thời gian thực (GetSeatingChartStatusAsync)
    // =========================================================================
    [Fact]
    public async Task GetSeatingChartStatusAsync_CorrectlyCalculatesHeldByMeAndHeldByOther()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestTimeProvider(BaselineTime);
        var service = new SeatReservationService(database.Context, clock);

        var seat1 = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "1", SeatStatus.Available);
        var seat2 = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "2", SeatStatus.Available);
        var seat3 = await CreateSeatAsync(database.Context, performanceId: 1, row: "A", number: "3", SeatStatus.Sold);

        // User 101 giữ seat1
        await service.HoldSeatAsync(performanceId: 1, seatId: seat1.Id, userId: 101);

        // User 102 xem sơ đồ
        var chartForUser102 = await service.GetSeatingChartStatusAsync(performanceId: 1, currentUserId: 102);

        var display1 = chartForUser102.Single(s => s.Id == seat1.Id);
        var display2 = chartForUser102.Single(s => s.Id == seat2.Id);
        var display3 = chartForUser102.Single(s => s.Id == seat3.Id);

        Assert.Equal("HeldByOther", display1.DisplayStatus);
        Assert.False(display1.CanSelect);

        Assert.Equal("Available", display2.DisplayStatus);
        Assert.True(display2.CanSelect);

        Assert.Equal("Sold", display3.DisplayStatus);
        Assert.False(display3.CanSelect);

        // User 101 xem sơ đồ
        var chartForUser101 = await service.GetSeatingChartStatusAsync(performanceId: 1, currentUserId: 101);
        var display1ForUser101 = chartForUser101.Single(s => s.Id == seat1.Id);

        Assert.Equal("HeldByMe", display1ForUser101.DisplayStatus);
        Assert.True(display1ForUser101.CanSelect);
    }

    private static async Task<Seat> CreateSeatAsync(
        AppDbContext db,
        int performanceId,
        string row,
        string number,
        SeatStatus status)
    {
        var category = await db.SeatCategories
            .FirstOrDefaultAsync(c => c.PerformanceId == performanceId && c.NormalizedName == "STANDARD");

        if (category == null)
        {
            category = new SeatCategory
            {
                PerformanceId = performanceId,
                Name = "Standard",
                NormalizedName = "STANDARD"
            };
            db.SeatCategories.Add(category);
            await db.SaveChangesAsync();
        }

        var seat = new Seat
        {
            PerformanceId = performanceId,
            SeatCategoryId = category.Id,
            Row = row,
            Number = number,
            Status = status
        };

        db.Seats.Add(seat);
        await db.SaveChangesAsync();
        return seat;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset currentUtc = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => currentUtc;

        public void Advance(TimeSpan duration)
        {
            currentUtc = currentUtc.Add(duration);
        }
    }
}

