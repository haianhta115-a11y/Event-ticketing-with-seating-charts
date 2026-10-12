# Event Ticketing System

## Yêu cầu hệ thống
- .NET 10 SDK
- Docker & Docker Compose

## Khởi động dự án trên máy cá nhân
1. Khởi động cơ sở dữ liệu (PostgreSQL và Redis):
   ```bash
   docker-compose up -d
   ```

2. Áp dụng migration vào database:
   ```bash
   dotnet ef database update --project EventTicketing.Api/EventTicketing.Api.csproj --startup-project EventTicketing.Api/EventTicketing.Api.csproj
   ```

3. Chạy ứng dụng:
   ```bash
   dotnet run --project EventTicketing.Api/EventTicketing.Api.csproj
   ```

Ứng dụng sẽ tự động đọc chuỗi kết nối từ biến môi trường hoặc cấu hình trong `appsettings.json`.
