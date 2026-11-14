# Remote Print Manager

Remote Print Manager là ứng dụng WPF cho Windows, giúp máy con gửi lệnh in đến máy chủ đã được ghép nối. Người dùng chỉ cần tải `RemotePrintManager.exe`, chọn vai trò, thiết lập kết nối và in bằng `Ctrl + P`.

## Tính năng chính

- Một tệp `RemotePrintManager.exe` self-contained cho Windows 10/11 x64; máy khách không cần cài .NET Runtime hoặc .NET SDK.
- Hai vai trò dùng chung một ứng dụng: **Máy chủ** và **Máy con**.
- Ghép nối bằng mã hiển thị trên màn hình chính và phê duyệt thiết bị tại máy chủ.
- Chọn máy in từ danh sách Windows; máy in được chọn tự động trở thành máy in mặc định của ứng dụng.
- Bật/tắt chia sẻ từng máy in bằng công tắc.
- Theo dõi thiết bị, hàng đợi in, kết nối và chẩn đoán trong màn hình cài đặt gọn.
- Cập nhật bằng manifest có SHA-256 và chữ ký bản phát hành; cấu hình người dùng nằm ngoài thư mục ứng dụng.
- Giữ lại lựa chọn máy in khi chuyển qua lại giữa các tab.

## Yêu cầu sử dụng

- Windows 10 hoặc Windows 11 64-bit.
- Máy chủ có Windows Print Spooler, driver máy in và máy in hoạt động.
- Máy chủ và máy con có kết nối mạng phù hợp với endpoint đã cấu hình.
- Không mở dịch vụ nội bộ ra Internet công cộng.

Máy con không được dùng `localhost` hoặc `127.0.0.1` làm địa chỉ máy chủ từ xa. Nếu chưa xác minh được máy chủ, ứng dụng hiển thị **Chưa kết nối**.

## Tải và chạy nhanh

1. Tải `RemotePrintManager.exe` từ [GitHub Releases](https://github.com/hnam1204/PRINT_EXE/releases).
2. Sao chép tệp vào máy cần sử dụng và mở tệp.
3. Chọn **Máy chủ** hoặc **Máy con** ở lần chạy đầu tiên.
4. Làm theo hướng dẫn ghép nối bên dưới.

## Thiết lập Máy chủ

Máy chủ là máy đang kết nối trực tiếp với máy in vật lý.

1. Mở `RemotePrintManager.exe` và chọn **Máy chủ**.
2. Vào **Cài đặt > Máy in**.
3. Chọn máy in cần chia sẻ trong danh sách.
4. Máy in được chọn sẽ tự động là máy in mặc định của ứng dụng; không có nút **Mặc định** riêng.
5. Bật công tắc chia sẻ của máy in.
6. Sao chép mã ghép nối hiển thị trên màn hình chính và gửi mã cho máy con.
7. Vào **Cài đặt > Thiết bị**, chọn yêu cầu mới và phê duyệt thiết bị.

## Thiết lập Máy con

1. Mở cùng tệp `RemotePrintManager.exe` trên máy con.
2. Chọn **Máy con**.
3. Nhập mã ghép nối trên màn hình chính.
4. Gửi yêu cầu kết nối và chờ máy chủ phê duyệt.
5. Khi trạng thái là **Đã kết nối**, máy con có thể gửi lệnh in đến máy in được cấp quyền.

## In tài liệu

1. Mở tài liệu trong Word, Excel, trình duyệt, PDF hoặc ứng dụng cần in.
2. Nhấn `Ctrl + P`.
3. Chọn máy in đã được cấu hình.
4. Chọn tùy chọn in và nhấn **In**.

Lệnh in chỉ được chuyển khi máy con đã ghép nối, được máy chủ phê duyệt và máy chủ đang hoạt động.

## Cài đặt và chẩn đoán

Màn hình **Cài đặt chung** chỉ hiển thị tên thiết bị, vai trò, trạng thái kết nối thực tế, phiên bản và tùy chọn khởi động cùng Windows.

Thông tin kỹ thuật như endpoint, IP, WebSocket và cổng kết nối chỉ xem trong **Chẩn đoán** hoặc **Cấu hình nâng cao**. Không chia sẻ mã ghép nối hoặc thông tin chẩn đoán lên nơi công khai.

## Cập nhật

1. Mở **Cài đặt**.
2. Tại dòng **Cập nhật**, nhấn **Kiểm tra cập nhật**.
3. Xem nội dung chi tiết trong hộp thoại riêng nếu có phiên bản mới.
4. Thực hiện cập nhật theo hướng dẫn và không tắt ứng dụng trong quá trình thay thế tệp.

Cấu hình được lưu tại `%APPDATA%\\RemotePrintManager\\config.json` và dữ liệu hỗ trợ cập nhật tại `%APPDATA%\\RemotePrintManager\\Updates`. Các dữ liệu này không nằm trong tệp EXE nên được giữ lại khi cập nhật.

## Kiểm tra tệp phát hành

Sau khi tải bản phát hành, có thể kiểm tra SHA-256 bằng PowerShell:

```powershell
Get-FileHash -Path .\\RemotePrintManager.exe -Algorithm SHA256
```

Đối chiếu giá trị với `sha256` trong `version.json` của bản phát hành.

## Build và publish từ source

### Môi trường build

- Windows 10/11 x64.
- .NET 9 SDK.
- PowerShell 5.1 hoặc PowerShell 7.

### Tạo bản phát hành

Đặt khóa ký manifest ở một trong hai vị trí sau, không commit khóa vào Git:

```powershell
New-Item -ItemType Directory -Force .\\secrets
$env:RPM_UPDATE_PRIVATE_KEY_PEM = Get-Content -Raw .\\secrets\\update-private-key.pem
```

Chạy script build:

```powershell
.\\build.ps1 -Version "1.0.1" -Channel "Beta"
```

Script sẽ chạy test, publish self-contained single-file cho ứng dụng và updater, tạo manifest có chữ ký, rồi chuẩn bị các tệp trong `publish/`:

- `publish/RemotePrintManager.exe`: ứng dụng chính cho khách hàng.
- `publish/RPM.Updater.exe`: updater độc lập dùng cho tương thích và chẩn đoán.
- `publish/version.json`: manifest phiên bản, URL tải, SHA-256 và chữ ký.
- `publish/RemotePrintManager_v<version>_Portable.zip`: gói portable.
- `publish/relay/RPM.Relay.exe`: relay broker tùy chọn cho môi trường được quản trị.

Bản publish chính đã đóng gói .NET Runtime và thư viện native cần thiết vào EXE. Máy khách không cần cài .NET riêng.

## Kiểm thử

```powershell
dotnet test --configuration Release
dotnet build RemotePrintManager.sln --configuration Release
```

Build self-contained local:

```powershell
dotnet publish src/RPM.App/RPM.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish\\app
```

Kiểm thử trên máy Windows sạch chưa cài .NET Runtime và kiểm thử Host/Client trên hai máy vật lý là các bước xác nhận riêng; không thay thế bằng kết quả build tại máy phát triển.

## Cấu trúc chính

```text
PRINTEXE/
├── src/
│   ├── RPM.App/          # WPF application
│   ├── RPM.Core/         # Models, configuration, update services
│   ├── RPM.Networking/   # Networking and relay integration
│   ├── RPM.Printing/     # Windows printer integration
│   ├── RPM.Relay/        # Optional relay broker
│   └── RPM.Updater/      # Update helper
├── tests/RPM.Tests/      # Automated tests
├── docs/
│   ├── USER_MANUAL.md
│   └── DEPLOYMENT_AND_OPERATIONS.md
├── build.ps1
└── RemotePrintManager.sln
```

## Tài liệu

- [Hướng dẫn người dùng](docs/USER_MANUAL.md)
- [Hướng dẫn triển khai và vận hành](docs/DEPLOYMENT_AND_OPERATIONS.md)

## GitHub workflow

Kiểm tra remote hiện tại:

```powershell
git remote -v
git status
```

Commit riêng phần tài liệu:

```powershell
git add README.md docs/USER_MANUAL.md
git commit -m "docs: update usage and release instructions"
```

Đẩy branch hiện tại lên GitHub:

```powershell
git push origin main
```

Khi phát hành phiên bản mới, tạo tag sau khi đã build và kiểm tra:

```powershell
git tag -a v1.0.1 -m "Release v1.0.1"
git push origin v1.0.1
```

Không commit `secrets/update-private-key.pem`, biến môi trường chứa khóa ký, hoặc các tệp cấu hình có thông tin bí mật.

## Giấy phép

Xem thông tin giấy phép và điều khoản sử dụng trong repository trước khi phân phối cho khách hàng.
