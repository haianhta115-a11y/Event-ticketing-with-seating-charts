# Event Ticketing System

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

Giữ hỗ trợ biến `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD`,
`REDIS_CONNECTION` trong cấu hình Staging. Full connection string cũng có thể được cấp
qua environment. Staging Compose cần `JWT_KEY` từ secret store/environment.
Không có fallback localhost/credential cho Production thiếu cấu hình.

Migration phải là thao tác được duyệt riêng, kiểm tra destination trước khi thực hiện.
Batch 1A không chạy migration; migration/HasData cũ có demo accounts và chưa được sửa
trong batch này. Không áp dụng chúng lên Neon trong công việc local/testing.
Không thay đổi migration lịch sử hoặc database của nhóm. Không dùng `Testing` để trỏ
vào database Development; guard yêu cầu tiền tố database riêng và Redis database 15.
