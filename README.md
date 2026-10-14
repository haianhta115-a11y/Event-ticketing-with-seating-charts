# Event Ticketing System

## Team local setup — Delivery 02 candidate

Start with [the Windows 11 clean-setup guide](docs/team-ready/LOCAL-SETUP.md), not
the historical migration chain or PRODUCT-01 private fixtures. These changes are
still uncommitted: current `develop` will not contain them until Delivery 02 is approved and merged.
Future shared database work requires the separate [Neon compatibility checklist](docs/team-ready/NEON-COMPATIBILITY.md).

## Batch 1A: môi trường local an toàn

Dùng .NET 10, Node/npm và Docker Desktop đã cài. PostgreSQL 15 và Redis 7 chạy
trong `docker-compose.yml`; API chạy bằng .NET trên Windows. Không dùng Neon
cho Development hoặc Testing.

### Cấu hình trước khi chạy

ASP.NET Core đọc JSON theo environment, rồi environment variables, rồi command-line.
Guard kiểm tra giá trị cuối cùng trước khi đăng ký database/Redis. Development và
Testing chỉ chấp nhận `localhost` hoặc IP loopback; hostname Docker `db`/`redis`,
remote host, multi-host PostgreSQL và Redis Sentinel bị từ chối trong hai environment này.
Không dùng port-forward/proxy loopback tới database remote.

Development mặc định dùng PostgreSQL `127.0.0.1:5432`, database `event_ticketing`,
và Redis `127.0.0.1:6379`. Cung cấp `DB_USER` và `DB_PASSWORD` bằng environment
variables; không lưu secret trong mã nguồn. Có thể ghi đè toàn bộ bằng
`ConnectionStrings__DefaultConnection` và `Redis__ConnectionString`.

Nhập credential của database local hiện có trong PowerShell, không ghi literal vào lịch sử:

```powershell
$env:DB_USER = Read-Host 'Local PostgreSQL username'
$localPassword = Read-Host 'Local PostgreSQL password' -AsSecureString
$env:DB_PASSWORD = [System.Net.NetworkCredential]::new('', $localPassword).Password
$env:Jwt__Key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:ASPNETCORE_ENVIRONMENT = 'Development'
```

Key ngẫu nhiên trên chỉ tồn tại trong process/shell; đổi key làm token cũ không còn hợp lệ.
Muốn duy trì phiên qua restart, cấp key ổn định qua secret management, không commit key.
`Jwt__Key` hoặc `JWT_KEY` phải dài ít nhất 32 bytes; ứng dụng không có signing key mặc định.
Nếu credential chứa dấu phân cách connection string, dùng full connection string đã quote
đúng qua `ConnectionStrings__DefaultConnection` thay vì template.

### Docker và startup

```powershell
docker info
docker compose config --quiet
# Chỉ start container có sẵn, giữ nguyên volume:
docker compose start db redis
```

Nếu Engine không hoạt động, mở Docker Desktop và dừng bước chạy app cho tới khi Engine
sẵn sàng. Nếu chưa có container, kiểm tra volume/port trước khi dùng `docker compose up -d`.
Không chạy `down -v`, reset hoặc recreate dữ liệu để giải quyết lỗi credential.
Thay DB_PASSWORD của Compose không tự đổi password trong volume PostgreSQL đã tồn tại.
Port mới tạo được bind loopback; container cũ chỉ start sẽ giữ nguyên port binding cũ.

```powershell
dotnet build EventTicketing.Api/EventTicketing.Api.csproj
dotnet run --project EventTicketing.Api/EventTicketing.Api.csproj --no-launch-profile -- --urls http://127.0.0.1:5009
```

`/health` là liveness. `/health/ready` thực hiện PostgreSQL `SELECT 1` và Redis `PING`;
HTTP 200 xác nhận kết nối, không xác nhận schema hoặc nghiệp vụ đã hoàn chỉnh.
Không log password/full connection string hoặc output `docker inspect` chứa credentials.

Startup không migrate, không seed, không reset admin, không chèn ghế debug.
Cleanup mặc định tắt trong Development/Testing. Chỉ bật
`BackgroundJobs__SeatHoldCleanupEnabled=true` sau khi xác minh DB/schema và chủ động
cho phép job ghi dữ liệu. Staging/Production giữ mặc định chạy cleanup như trước.

### Thời hạn giữ ghế (S-10)

`SeatHolds__DurationMinutes` cấu hình số phút của một phiên giữ mới, mặc định 10.
Startup từ chối giá trị ngoài 1–1440 phút để tránh cấu hình thời hạn không an toàn.
Đổi cấu hình không gia hạn hoặc rút ngắn phiên đang tồn tại: các ghế chọn thêm,
reload và nhả bớt ghế vẫn dùng deadline đã lưu trên máy chủ. UI đếm theo server time.

FINAL-FIX: mặc định bấm ghế trống sẽ giữ ngay; bấm lại ghế mình giữ sẽ nhả.
Muốn giữ nhiều ghế atomically, bật “Chọn nhiều ghế — chỉ giữ khi xác nhận cả nhóm”.
Ghế chờ trong chế độ này chưa phải hold; xác nhận mới gửi một batch all-or-nothing.
Release thành công trả snapshot chart/session trong cùng DELETE, không cần GET bổ
sung; lỗi/mất response vẫn refresh từ máy chủ, không tự đoán quyền sở hữu.
Thời gian reservation/expiry dùng PostgreSQL clock_timestamp; browser dùng monotonic
clock + server timestamp, không dùng localStorage deadline hoặc thời gian máy khách.

### Sơ đồ mẫu và phép đo local (T-21)

`samples/seating-chart-2000.json` là fixture tổng hợp, không phải dữ liệu lịch sử.
`node scripts/generate-seat-chart.mjs 100 samples/new-demo-100.json` sinh file mới
và từ chối ghi đè. Chỉ preview/import vào showtime test riêng được cho phép.

Phép đo Chrome local FINAL-FIX (2026-10-14): 1 warmup + 20 lần điều hướng từ request
trang đến đủ 2.000 SVG seat nodes và 2 animation frames; p95 **775,67 ms**, dưới 2s.
Đây là Vite dev, warm assets, desktop headless; không phải SLA production hoặc điện
thoại vật lý. Các lần chạy và phép đo SQL được lưu trong báo cáo FINAL-FIX.
Sample đã qua chính API multipart preview (không ghi) và confirmed import trong
fixture DB mới. Service query 2.000 ghế, gồm access check và DB clock, có 3 warmup +
30 samples; p95 **40,17 ms**, dưới 200ms trên PostgreSQL local. Không suy ra SLA tải cao.

Frontend có `npm run typecheck --prefix frontend` (checkJs toàn src, JSDoc, noEmit).
CI tách lint song song tests; gate fail-closed nối backend/frontend/lint. Coverage JS
so với baseline SHA rõ ràng, từ chối missing artifact/regression; JSX và baseline C#
vẫn cần evidence riêng. Manual CI yêu cầu SHA tổ tiên rõ ràng, không fallback commit
với chính nó. Xem [staging approval](docs/finalfix/STAGING-APPROVAL.md): deploy bị khóa
mặc định, chưa thực hiện; không migrations/seeds/shared database trong FINAL-FIX.

Delivery 02 vẫn là code-only: cấu hình này không cho phép tự chạy historical EF
migrations, seed, DB-03B hoặc triển khai lên database team. Rehearsal/test chỉ dùng
database local cô lập được duyệt và Redis DB 15; không dùng source/protected DB.

### Kiểm tra Batch 1A

```powershell
dotnet run --project tests/EnvironmentSafetyChecks/EnvironmentSafetyChecks.csproj --no-launch-profile
```

Lệnh trên chỉ kiểm guard cấu hình, không kết nối database. Smoke test yêu cầu một database
local rỗng mới với tên `event_ticketing_test_<unique suffix>` và Redis database 15.
Cấp full connection string tới database đó qua `ConnectionStrings__DefaultConnection`,
cấp `Redis__ConnectionString=127.0.0.1:6379,defaultDatabase=15`, rồi chạy:

```powershell
dotnet run --project tests/EnvironmentSafetyChecks/EnvironmentSafetyChecks.csproj --no-launch-profile -- --smoke
```

Runner kiểm destination trước khi mở kết nối, chạy API lần lượt Development/Testing,
kiểm readiness và xác nhận database vẫn không có bảng; không migrate/seed/reset.
Không chạy bộ test nghiệp vụ cũ trong Batch 1A: còn model Performance cũ và fixture SQLite/InMemory.

### Deployment và migrations

JSON Development/Testing/Staging yêu cầu `POSTGRES_CONNECTION_STRING` hoặc override
`ConnectionStrings__DefaultConnection` qua environment, không còn credential literal.
Staging Compose vẫn có thể ghép `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD`
thành override connection string; các biến này không tự được ghép khi chạy DLL riêng.
Redis cấp qua `Redis__ConnectionString`/`REDIS_CONNECTION`; Staging Compose cần `JWT_KEY`
từ secret store/environment.
Không có fallback localhost/credential cho Production thiếu cấu hình.

Migration phải là thao tác được duyệt riêng, kiểm tra destination trước khi thực hiện.
Batch 1A không chạy migration; migration/HasData cũ có demo accounts và chưa được sửa
trong batch này. Không áp dụng chúng lên Neon trong công việc local/testing.
Không thay đổi migration lịch sử hoặc database của nhóm. Không dùng `Testing` để trỏ
vào database Development; guard yêu cầu tiền tố database riêng và Redis database 15.
