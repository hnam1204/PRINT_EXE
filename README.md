# Remote Print Manager (RPM)

Phần mềm chia sẻ máy in qua Internet độc lập dành cho văn phòng chi nhánh và phòng ban trong bán kính 1 km.

---

## 🎯 Điểm Nổi Bật

1. **Bộ Cài Đặt Duy Nhất:** Một file `.exe` duy nhất cho cả Máy Chủ và 3 Máy Con. Khởi động lần đầu chỉ cần chọn vai trò.
2. **In Trực Tiếp Qua Phím `Ctrl + P`:** Nhân viên mở Word, Excel, Trình duyệt web... nhấn `Ctrl + P`, chọn máy in và bấm In. Tài liệu tự động truyền qua Internet đến máy in USB tại máy chủ.
3. **Kết Nối Internet Độc Lập:** Không yêu cầu chung mạng Wi-Fi, không cần kéo dây mạng, không cần mở cổng Router (Port Forwarding), hoạt động mượt mà dù các máy dùng 4G hay nhà mạng khác nhau.
4. **Mã Hóa Đầu Cuối (E2EE):** Bảo mật toàn bộ dữ liệu in bằng thuật toán **AES-256-GCM** cấp ngân hàng.
5. **Chống In Trùng & Điều Phối Hàng Đợi:** Tự động kiểm tra mã băm SHA-256 và điều phối thứ tự in FIFO.
6. **Chạy Ngầm Khay Hệ Thống:** Tiêu thụ cực ít tài nguyên (dưới 50MB RAM), thu nhỏ xuống góc màn hình và thông báo khi hoàn thành.

---

## 📂 Cấu Trúc Dự Án

```
PRINTEXE/
├── src/
│   ├── RPM.Core/          # Data models, E2EE AES-256-GCM, SHA-256, giao thức gói tin, cấu hình
│   ├── RPM.Printing/      # Windows Spooler helper (winspool.drv), Virtual IPP Spooler, Discovery
│   ├── RPM.Networking/    # WebSocket Relay Client, ghép đôi Pairing Code, mã hóa luồng in
│   ├── RPM.Relay/         # Máy chủ trung chuyển đám mây siêu nhẹ (Cloud Relay Broker)
│   └── RPM.App/           # Ứng dụng desktop WPF Modern Fluent UI (Server & Client Dashboard, System Tray)
├── tests/
│   └── RPM.Tests/         # Bộ kiểm thử tự động 100% Pass (E2E handshake, Crypto, IPP)
├── installer/
│   └── RemotePrintManager.iss # Kịch bản đóng gói bộ cài Inno Setup
├── publish/
│   ├── RemotePrintManager.exe               # File chạy trực tiếp duy nhất
│   ├── RemotePrintManager_v1.0.0_Portable.zip # Gói nén portable sẵn sàng copy
│   └── relay/RPM.Relay.exe                  # Standalone Cloud Relay Broker
└── build.ps1              # Kịch bản tự động test, build và đóng gói
```

---

## 🚀 Hướng Dẫn Sử Dụng Nhanh

### 1. Tại Máy Tính Chủ (Server - Máy cắm dây USB vào máy in)
1. Chạy file `RemotePrintManager.exe`.
2. Lần đầu mở, chọn **🖥️ Chế Độ Máy Chủ**.
3. Vào tab **Máy In** -> Chọn máy in USB của công ty -> Bấm **"Chọn làm máy in chia sẻ"**.
4. Vào tab **Tổng quan** -> Sao chép **Mã Ghép Đôi (Pairing Code)** (Ví dụ: `RPM-8821`) và gửi cho 3 máy con.
5. Khi máy con xin kết nối, vào tab **Thiết Bị Kết Nối** -> Bấm **"Phê duyệt (Approve)"**.

### 2. Tại 3 Máy Tính Con (Clients - Trong bán kính 1 km)
1. Chạy file `RemotePrintManager.exe` trên từng máy con.
2. Lần đầu mở, chọn **💻 Chế Độ Máy Con**.
3. Vào tab **Tìm & Kết Nối Máy Chủ** -> Nhập mã ghép đôi (Ví dụ: `RPM-8821`) -> Bấm **"Gửi Yêu Cầu Ghép Đôi"**.
4. Sau khi Máy Chủ bấm phê duyệt, vào tab **Máy In Của Tôi** -> Bấm **"Cài Đặt Máy In Ảo Vào Windows"**.
5. Mở tài liệu bất kỳ trong Word, Excel, Chrome... nhấn tổ hợp phím **Ctrl + P** -> Chọn máy in công ty -> Nhấn **Print**!

---

## 🛠️ Biên Dịch & Đóng Gói

Yêu cầu môi trường: .NET 9 SDK trên Windows.

```powershell
# Chạy script tự động test và đóng gói:
.\build.ps1
```

