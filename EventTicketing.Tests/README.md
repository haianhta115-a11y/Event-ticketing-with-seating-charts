# Baseline kiểm thử — Batch 1A.1

Solution chính gồm EventTicketing.Api và EventTicketing.Tests. Các bản nguồn thử nghiệm
trong src/backend và Event-ticketing-with-seating-charts-main không thuộc solution.

## Build và unit tests

```powershell
dotnet build EventTicketing.slnx --no-restore -m:1
dotnet test EventTicketing.Tests/EventTicketing.Tests.csproj --no-build --no-restore --filter 'Category=Unit'
```

Category=Unit gồm validator thuần, không mở database/Redis. Integration tests không dùng
SQLite hoặc InMemory; chúng kiểm tra model, constraints và transactions trên PostgreSQL.
Giữ toàn bộ 52 test và assertion gốc. Tên parameter/entity/route được cập nhật theo
Showtime; các assertion nghiệp vụ không được đổi để che lỗi production.

## Cấu hình integration local

Docker Engine và các container PostgreSQL/Redis local phải đang hoạt động. Xác minh
port binding, pg_isready và Redis PING trước khi chạy. Không print docker inspect hoặc
full connection string vì có thể chứa credential.

Trong PowerShell, nhập connection string local có credential qua secure prompt:

```powershell
$testConnection = Read-Host 'Verified local PostgreSQL connection string' -AsSecureString
$env:BATCH1A1_POSTGRES = [System.Net.NetworkCredential]::new('', $testConnection).Password
$env:BATCH1A1_REDIS = '127.0.0.1:6379,defaultDatabase=15'
dotnet test EventTicketing.slnx --no-build --no-restore --logger 'trx;LogFileName=batch1a1-final.trx'
```

Connection string phải có Host loopback và Database mang tiền tố event_ticketing_test_.
Database trong cấu hình là giá trị kiểm guard, không dùng để lưu fixture. Mỗi fixture
tạo database mới event_ticketing_test_1a1_<GUID> trên cùng server local đã xác minh;
role local cần quyền CREATE DATABASE và kết nối database quản trị postgres.

LocalPostgresDatabase gọi guard Batch 1A trước mọi kết nối, kiểm Redis PING, rồi tạo
schema từ model production chỉ trong database mới. Nó loại các data operations HasData,
không chạy migrations hoặc seed ứng dụng. Dữ liệu tối thiểu (Event, Showtime, Buyer role)
được tạo rõ ràng bởi từng test theo yêu cầu FK. Không reset/drop database hoặc xoá volume.
Database mới được giữ lại để điều tra; việc dọn chúng cần phê duyệt riêng.

LocalApplicationFactory nạp cấu hình vào host trước safety checks của minimal Program,
dùng signing key ngẫu nhiên cho riêng test host và giữ cleanup tắt. Không thay environment
variables toàn process trong từng factory. Không nới guard hoặc mock production service.

Test concurrent dùng hai DbContext/connection riêng và barrier sau hai lần đọc ghế,
buộc cả hai request quan sát cùng trạng thái trước mutation. Không dùng chung connection
để giả lập PostgreSQL concurrency.

## Kết quả ngày 2026-10-10

- Build solution: thành công, 0 lỗi và 0 warning trong lần build kiểm chứng.
- Unit: 18 pass, 0 fail, 0 skipped.
- Integration: 30 pass, 4 fail, 0 skipped.
- Tổng: 48 pass, 4 fail, 0 skipped / 52 test.

Bốn assertion fail phản ánh ba vấn đề production đã tái hiện, không phải lỗi fixture:

1. Service và endpoint trả thông báo khác `ghế vừa có người chọn` (hai test).
2. Ghế chọn thêm có expiry mới thay vì deadline của ghế đầu (một test).
3. Hai request cùng đọc ghế Available đều trả thành công (một test).

Các assertion này được giữ nguyên. Batch 1A.1 không sửa production business logic.
Hai test registration hiện là kiểm tra hành vi legacy; chúng chưa chứng minh AC đầy đủ
của S-03 (pending activation, generic response, one-time token, TTL và resend limit).
Import service tests không chứng minh controller import thực tế dùng service đó.

TRX nằm trong TestResults (Git ignored). Test failure phải được báo riêng với compiler
failure hoặc lỗi cấu hình/dependency. Không coi baseline hiện tại là toàn bộ AC đã đạt.
