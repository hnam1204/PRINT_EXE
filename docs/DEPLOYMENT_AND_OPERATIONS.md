# Hướng Dẫn Toàn Diện: Tải Về, Cài Đặt, Vận Hành, Cập Nhật, Đóng Gói & Triển Khai Lên GitHub
## Remote Print Manager (RPM) — Hệ Thống Chia Sẻ Máy In Bán Kính 1 KM Qua Internet Độc Lập

---

## 📑 Mục Lục

1. [Tổng Quan Kiến Trúc & Kịch Bản Văn Phòng](#1-tổng-quan-kiến-trúc--kịch-bản-văn-phòng)
2. [Cách Tải Về & Kiểm Tra Tính Toàn Vẹn](#2-cách-tải-về--kiểm-tra-tính-toàn-vẹn)
3. [Hướng Dẫn Cài Đặt & Cấu Hình Lần Đầu](#3-hướng-dẫn-cài-đặt--cấu-hình-lần-đầu)
   - [3.1. Thiết lập Máy Chủ (Server - Máy cắm dây USB vào máy in)](#31-thiết-lập-máy-chủ-server---máy-cắm-dây-usb-vào-máy-in)
   - [3.2. Thiết lập 3 Máy Con (Clients - Trong bán kính 1 km)](#32-thiết-lập-3-máy-con-clients---trong-bán-kính-1-km)
   - [3.3. Phê duyệt & Cấp quyền thiết bị](#33-phê-duyệt--cấp-quyền-thiết-bị)
4. [Hướng Dẫn Vận Hành & In Ấn Hàng Ngày](#4-hướng-dẫn-vận-hành--in-ấn-hàng-ngày)
   - [4.1. In ấn trực tiếp qua Ctrl + P từ mọi phần mềm](#41-in-ấn-trực-tiếp-qua-ctrl--p-từ-mọi-phần-mềm)
   - [4.2. Quản lý hàng đợi in & Cơ chế chống in trùng](#42-quản-lý-hàng-đợi-in--cơ-chế-chống-in-trùng)
   - [4.3. Chạy ngầm khay hệ thống (System Tray)](#43-chạy-ngầm-khay-hệ-thống-system-tray)
5. [Vận Hành Máy Chủ Trung Gian (Relay Cloud Broker)](#5-vận-hành-máy-chủ-trung-gian-relay-cloud-broker)
   - [5.1. Chế độ Relay nhúng tự động (Mặc định - 0 cấu hình)](#51-chế-độ-relay-nhúng-tự-động-mặc-định---0-cấu-hình)
   - [5.2. Chế độ Relay đám mây trên VPS/Cloud (Docker / Linux Server)](#52-chế-độ-relay-đám-mây-trên-vpscloud-docker--linux-server)
6. [Quy Trình Cập Nhật & Nâng Cấp Phần Mềm Trực Tiếp (Update & Upgrade)](#6-quy-trình-cập-nhật--nâng-cấp-phần-mềm-trực-tiếp-update--upgrade)
   - [6.1. Thao tác 1-Click Update trên giao diện người dùng](#61-thao-tác-1-click-update-trên-giao-diện-người-dùng)
   - [6.2. Quy trình phát hành bản cập nhật mới (Dành cho Developer/Admin)](#62-quy-trình-phát-hành-bản-cập-nhật-mới-dành-cho-developeradmin)
   - [6.3. Quy trình thử nghiệm từng bước (Canary Rollout)](#63-quy-trình-thử-nghiệm-từng-bước-canary-rollout)
   - [6.4. Cơ chế bảo toàn dữ liệu cấu hình 100%](#64-cơ-chế-bảo-toàn-dữ-liệu-cấu-hình-100)
7. [Hướng Dẫn Đóng Gói Ứng Dụng (Build & Packaging)](#7-hướng-dẫn-đóng-gói-ứng-dụng-build--packaging)
   - [7.1. Đóng gói nhanh bằng kịch bản build.ps1](#71-đóng-gói-nhanh-bằng-kịch-bản-buildps1)
   - [7.2. Tạo bộ cài đặt Windows chuyên nghiệp (Inno Setup)](#72-tạo-bộ-cài-đặt-windows-chuyên-nghiệp-inno-setup)
8. [Hướng Dẫn Triển Khai Toàn Diện Lên GitHub (GitHub Deployment & CI/CD)](#8-hướng-dẫn-triển-khai-toàn-diện-lên-github-github-deployment--cicd)
   - [8.1. Đẩy mã nguồn lên kho chứa GitHub](#81-đẩy-mã-nguồn-lên-kho-chứa-github)
   - [8.2. Thiết lập GitHub Actions tự động build và release khi gắn Tag](#82-thiết-lập-github-actions-tự-động-build-và-release-khi-gắn-tag)
   - [8.3. Cấu hình tự động cập nhật qua GitHub Releases](#83-cấu-hình-tự-động-cập-nhật-qua-github-releases)
9. [Xử Lý Sự Cố Thường Gặp (Troubleshooting & FAQs)](#9-xử-lý-sự-cố-thường-gặp-troubleshooting--faqs)

---

## 1. Tổng Quan Kiến Trúc & Kịch Bản Văn Phòng

### Bài toán thực tế
* **01 Máy tính chủ (Server):** Đặt tại phòng hành chính, cắm dây cáp USB trực tiếp vào máy in (ví dụ: Canon LBP 2900, HP LaserJet, Brother, Epson...).
* **03 Máy tính con (Clients):** Nằm rải rác ở các phòng ban khác nhau, tầng khác nhau hoặc tòa nhà lân cận trong **phạm vi bán kính 1 km**.
* **Môi trường mạng:** Các máy sử dụng các đường truyền Internet hoàn toàn độc lập (nhà mạng khác nhau như Viettel, VNPT, FPT, hoặc 4G/5G).
* **Mục tiêu:** Không cần kéo dây mạng LAN, không cần mua router chia sẻ phức tạp, không cần đổi IP tĩnh hay NAT Port. Cả 3 máy con đều có thể in trực tiếp chỉ bằng lệnh `Ctrl + P`.

```
               ┌────────────────────────────────────────────────────────┐
               │         RELAY CLOUD BROKER (ws:// / wss://)             │
               │    - Tự động nhúng trên Máy Chủ (mặc định)            │
               │    - Hoặc chạy trên VPS Đám Mây (Ubuntu/Docker)         │
               └───────────────▲────────────────────────▲───────────────┘
                               │                        │
         Kết nối WebSocket     │                        │  Kết nối WebSocket
         Mã hóa AES-256-GCM    │                        │  Mã hóa AES-256-GCM
                               │                        │
                ┌──────────────┴──────────┐   ┌─────────┴───────────────────────┐
                │     MÁY TÍNH CHỦ        │   │    3 MÁY TÍNH CON (Bán kính 1km)│
                │  - Nhận luồng in        │   │  - Máy in ảo Windows            │
                │  - Chống in trùng SHA   │   │  - In trực tiếp qua Ctrl + P    │
                │  - Quản lý hàng đợi     │   │  - Tự động mã hóa & truyền file │
                └──────────────┬──────────┘   └─────────────────────────────────┘
                               │
                          Dây cáp USB
                               │
                        ┌──────▼──────┐
                        │   MÁY IN    │
                        │  VẬT LÝ     │
                        └─────────────┘
```

---

## 2. Cách Tải Về & Kiểm Tra Tính Toàn Vẹn

### 2.1. Tải về từ GitHub Releases
Người dùng và quản trị viên truy cập mục **Releases** của kho chứa GitHub dự án:
* **Tệp chạy trực tiếp (Single-File Exe):** `RemotePrintManager.exe` — Không cần cài đặt, nhấp đúp là khởi chạy ngay.
* **Gói nén di động (Portable Zip):** `RemotePrintManager_v1.0.0_Portable.zip` — Chứa đầy đủ tệp thực thi và tài liệu.
* **Bộ cài đặt chuyên nghiệp:** `RemotePrintManager_Setup_v1.0.0.exe` (nếu biên dịch qua Inno Setup).

### 2.2. Kiểm tra tính toàn vẹn (SHA-256 Checksum)
Để đảm bảo tệp tải về không bị lỗi đường truyền hoặc bị can thiệp bởi mã độc, mở PowerShell và chạy lệnh kiểm tra:

```powershell
Get-FileHash -Path ".\RemotePrintManager.exe" -Algorithm SHA256
```
So sánh chuỗi Hash trả về với chuỗi hash được công bố trong tệp `version.json` hoặc mục Release Note trên GitHub.

---

## 3. Hướng Dẫn Cài Đặt & Cấu Hình Lần Đầu

Chỉ sử dụng **một file `RemotePrintManager.exe` duy nhất** cho toàn bộ 4 máy tính.

### 3.1. Thiết lập Máy Chủ (Server - Máy cắm dây USB vào máy in)
1. Sao chép `RemotePrintManager.exe` vào máy tính chủ. Nhấp đúp để mở ứng dụng.
2. Tại màn hình lựa chọn vai trò lần đầu, chọn: **🖥️ THIẾT LẬP LÀM MÁY CHỦ (SERVER)**.
3. Phần mềm sẽ mở giao diện quản trị Fluent UI:
   * **Bước 1 — Chọn máy in USB:** Chuyển sang tab **"Máy In"**, nhấn nút **"Quét máy in trên máy tính"**. Chọn đúng dòng máy in đang kết nối qua cáp USB (ví dụ: *Canon LBP 2900*) và bấm **"Chọn làm máy in chia sẻ"**. Có thể bấm nút **"In trang kiểm tra (Test Print)"** để kiểm tra máy in phản hồi tốt.
   * **Bước 2 — Lấy mã ghép đôi (Pairing Code):** Chuyển về tab **"Tổng Quan"**. Nhìn vào khung màu xanh *Thông Tin Ghép Đôi*: bạn sẽ thấy một mã gồm 8 ký tự (ví dụ: `RPM-8197`). Bấm nút **"Sao chép mã"** và gửi mã này cho 3 máy con qua Zalo/Telegram/Email.

> [!NOTE]
> Hệ thống Máy Chủ mặc định đã tích hợp sẵn **Relay Server nhúng**. Bạn không cần mở thêm bất kỳ phần mềm trung gian nào khác.

---

### 3.2. Thiết lập 3 Máy Con (Clients - Trong bán kính 1 km)
Thực hiện lần lượt trên từng máy con:
1. Sao chép `RemotePrintManager.exe` vào máy con và mở ứng dụng.
2. Tại màn hình đầu tiên, chọn: **💻 THIẾT LẬP LÀM MÁY CON (CLIENT)**.
3. Chuyển sang tab **"Tìm & Kết Nối Máy Chủ"**:
   * Nhập mã ghép đôi nhận được từ máy chủ (ví dụ: `RPM-8197`).
   * Điền tên gợi nhớ cho máy tính của mình (ví dụ: *Kế Toán - Máy 01*).
   * Nhấn nút **"Gửi Yêu Cầu Ghép Đôi Tới Máy Chủ"**.
4. Chờ máy chủ bấm duyệt (xem mục 3.3). Ngay sau khi được duyệt:
   * Chuyển sang tab **"Máy In Của Tôi"**.
   * Nhấn nút **"Cài Đặt Máy In Ảo Vào Windows"**. Hệ thống sẽ tự động cấu hình một máy in ảo trên Windows có tên: `Remote Printer (RPM)`.

---

### 3.3. Phê duyệt & Cấp quyền thiết bị
Trên giao diện **Máy Chủ**:
1. Chuyển sang tab **"Thiết Bị Kết Nối"**.
2. Khi máy con gửi yêu cầu, danh sách sẽ hiển thị tên máy, địa chỉ IP và trạng thái `Chờ phê duyệt`.
3. Quản trị viên chỉ cần chọn thiết bị và bấm **"Phê duyệt (Approve)"**.
4. Máy con sẽ ngay lập tức chuyển sang trạng thái xanh: `● Đã kết nối & Sẵn sàng in`.

> [!TIP]
> Máy chủ có thể bấm **"Tạm dừng"** hoặc **"Thu hồi quyền in"** bất kỳ lúc nào nếu máy con có dấu hiệu in tài liệu rác hoặc nhân viên nghỉ việc.

---

## 4. Hướng Dẫn Vận Hành & In Ấn Hàng Ngày

Sau khi hoàn tất bước cài đặt ở mục 3, **nhân viên văn phòng hoàn toàn không cần mở phần mềm Remote Print Manager mỗi khi in tài liệu**.

### 4.1. In ấn trực tiếp qua Ctrl + P từ mọi phần mềm
1. Mở bất kỳ tài liệu nào: Microsoft Word (.docx), Excel (.xlsx), PDF, Hóa đơn điện tử trên Chrome/Edge, hoặc phần mềm kế toán (MISA, Fast...).
2. Nhấn tổ hợp phím quen thuộc: **`Ctrl + P`** (hoặc chọn Menu *File → Print*).
3. Trong hộp thoại chọn máy in của Windows, chọn máy in: **`Remote Printer (RPM)`** (hoặc tên máy in chia sẻ công ty).
4. Thiết lập các thông số in ấn như thông thường:
   * Số bản sao (Copies)
   * Khổ giấy (A4, Letter)
   * Hướng giấy (Dọc / Ngang)
   * In tất cả trang hoặc trang tùy chọn
5. Nhấn nút **Print (In)**.
6. Lệnh in sẽ được phần mềm bắt ngầm, nén và mã hóa đầu cuối AES-256-GCM, truyền qua Internet về máy chủ. Máy in cắm cáp USB tại máy chủ sẽ tự động nhả giấy in ngay lập tức!

---

### 4.2. Quản lý hàng đợi in & Cơ chế chống in trùng
* **Điều phối FIFO:** Khi 3 máy con bấm in cùng lúc, hệ thống tự động sắp xếp hàng đợi theo thứ tự trước sau, không bao giờ bị chèn trang hoặc lẫn lộn văn bản giữa các phòng ban.
* **Chống in trùng thông minh:** Nếu nhân viên vô tình bấm đúp nút Print hoặc do sự cố mạng chập chờn lệnh in bị gửi lặp lại trong vòng 60 giây, thuật toán SHA-256 sẽ phát hiện tệp tin trùng lặp và chặn in lần 2, giúp tiết kiệm giấy mực tối đa.

---

### 4.3. Chạy ngầm khay hệ thống (System Tray)
* Khi bấm nút đóng `[X]` hoặc nút **"Thu nhỏ"**, phần mềm sẽ tự động ẩn xuống khay đồng hồ góc phải màn hình Windows (`System Tray`).
* Phần mềm tiêu thụ tài nguyên cực kỳ tiết kiệm: **< 45 MB RAM**, CPU gần như 0%.
* Khi in xong tài liệu, hệ thống sẽ phát âm thanh nhẹ và hiển thị thông báo popup: *"Đã in tài liệu thành công"*.

---

## 5. Vận Hành Máy Chủ Trung Gian (Relay Cloud Broker)

### 5.1. Chế độ Relay nhúng tự động (Mặc định - 0 cấu hình)
* Phù hợp cho mạng văn phòng mà Máy Chủ có thể tiếp cận được qua IP mạng hoặc qua VPN (ZeroTier, Tailscale, Cloudflare Tunnel).
* Ứng dụng Máy Chủ sẽ tự động lắng nghe cổng WebSocket `9090`. Không cần mở thêm màn hình nào khác.

---

### 5.2. Chế độ Relay đám mây trên VPS/Cloud (Docker / Linux Server)
Khi 4 máy tính nằm tại các địa điểm hoàn toàn tách biệt, sử dụng các nhà mạng khác nhau không có chung VPN, triển khai một máy chủ Relay độc lập trên VPS đám mây (giá rẻ từ $3 - $5/tháng, hoặc miễn phí trên Oracle Cloud / DigitalOcean / Linode / Google Cloud):

#### Cách 1: Chạy bằng Docker Compose (Khuyên dùng)
Trên máy chủ Linux VPS:
```bash
git clone https://github.com/<your-username>/PRINTEXE.git
cd PRINTEXE/deploy
docker compose up -d
```
Container `rpm-relay-server` sẽ khởi chạy và mở cổng `9090`.

#### Cách 2: Chạy trực tiếp file nhị phân Linux / Windows
* **Trên Windows Server:** Chạy file `publish/relay/RPM.Relay.exe`.
* **Trên Ubuntu Server (Cài .NET 9 Runtime):**
  ```bash
  cd src/RPM.Relay
  dotnet run -c Release -- --port 9090
  ```

#### Cấu hình kết nối trên Máy Chủ & Máy Con
Vào tab **Cài Đặt** trên phần mềm Remote Print Manager:
* Thay đổi ô *Relay Server URL* từ `ws://127.0.0.1:9090` thành địa chỉ VPS của bạn:
  `ws://vps-ip-cua-ban:9090` (hoặc `wss://relay.tencongty.com` nếu có tên miền SSL).

---

## 6. Quy Trình Cập Nhật & Nâng Cấp Phần Mềm Trực Tiếp (Update & Upgrade)

Tính năng cập nhật trực tiếp cho phép nâng cấp phiên bản mới mà **không cần đến từng máy tính để cài đặt lại thủ công**.

### 6.1. Thao tác 1-Click Update trên giao diện người dùng
Tại cả Máy Chủ và 3 Máy Con:
1. Vào tab **"Cài Đặt"** → Khu vực **"Cập Nhật & Nâng Cấp Phần Mềm"**.
2. Phần mềm định kỳ tự động kiểm tra, hoặc người dùng nhấn nút **"🔍 Kiểm tra cập nhật"**.
3. Nếu có phiên bản mới (ví dụ V1.0.1), màn hình sẽ hiển thị:
   * Huy hiệu xanh: `Có bản cập nhật mới: V1.0.1`
   * Bấm **"📝 Xem nội dung thay đổi"** để đọc chi tiết nhật ký nâng cấp.
4. Bấm nút **"⚡ Cập nhật ngay"**:
   * Hệ thống tự động kiểm tra nếu đang có lệnh in dở dang, sẽ cảnh báo chờ hoàn tất.
   * Tiến trình tải xuống hiển thị trực quan từ 0% đến 100%.
   * Hệ thống kiểm tra mã băm SHA-256 tự động để đảm bảo an toàn tuyệt đối.
   * Tự động sao lưu bản cũ thành `RemotePrintManager.exe.bak`, hoán đổi tệp mới và khởi động lại.

---

### 6.2. Quy trình phát hành bản cập nhật mới (Dành cho Developer/Admin)
Khi bạn chỉnh sửa mã nguồn để vá lỗi hoặc bổ sung giao diện mới:
1. Mở PowerShell tại thư mục dự án và chạy kịch bản đóng gói:
   ```powershell
   .\build.ps1 -Version "1.0.1" -Changelog @(
       "Cải thiện tốc độ truyền file in khổ lớn",
       "Tự động kết nối lại khi mạng 4G/Wifi phục hồi",
       "Khắc phục sự cố bộ đệm Windows Spooler"
   )
   ```
2. Kịch bản sẽ tự động:
   * Chạy toàn bộ 8 bài Unit Test.
   * Biên dịch `RemotePrintManager.exe` mới.
   * Tính toán mã băm SHA-256 mới.
   * Xuất file `publish/version.json`.
3. Tải `RemotePrintManager.exe` và `version.json` lên máy chủ cập nhật (Relay Server hoặc GitHub Releases).

---

### 6.3. Quy trình thử nghiệm từng bước (Canary Rollout)
Để đảm bảo tính liên tục của công việc văn phòng:
1. **Bước 1:** Bấm cập nhật trên **Máy con số 1** trước tiên.
2. **Bước 2:** In thử nghiệm văn bản Word, Excel thực tế từ máy 1.
3. **Bước 3:** Sau khi xác nhận máy 1 in mượt mà, thông báo cho Quản trị viên và 2 máy con còn lại bấm cập nhật hoàn tất toàn hệ thống.

---

### 6.4. Cơ chế bảo toàn dữ liệu cấu hình 100%
Trong toàn bộ quá trình nâng cấp, tệp cấu hình `%AppData%\RemotePrintManager\config.json` được bảo vệ hoàn toàn:
* Vai trò Máy Chủ / Máy Con không bị mất.
* Tên máy in đã chia sẻ được giữ nguyên.
* Danh sách 3 máy con đã được phê duyệt không cần duyệt lại.
* Lịch sử nhật ký hoạt động được bảo toàn nguyên vẹn.

---

## 7. Hướng Dẫn Đóng Gói Ứng Dụng (Build & Packaging)

### Yêu cầu môi trường
* Hệ điều hành: Windows 10 / Windows 11 / Windows Server.
* Bộ công cụ: **.NET 9 SDK** (tải miễn phí tại [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0)).

### 7.1. Đóng gói nhanh bằng kịch bản build.ps1
Mở PowerShell tại thư mục gốc dự án và thực hiện:
```powershell
# Cho phép thực thi script tạm thời nếu bị chặn
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Chạy build với phiên bản mong muốn
.\build.ps1 -Version "1.0.0"
```
Kết quả xuất hiện trong thư mục `publish/`:
* `publish/RemotePrintManager.exe` (Single-file chạy độc lập, ~380KB)
* `publish/RemotePrintManager_v1.0.0_Portable.zip` (Gói nén gọn gàng)
* `publish/version.json` (Tệp cấu hình cập nhật SHA-256)
* `publish/relay/RPM.Relay.exe` (Máy chủ Relay độc lập)

---

### 7.2. Tạo bộ cài đặt Windows chuyên nghiệp (Inno Setup)
Nếu muốn đóng gói thành file cài đặt có trình hướng dẫn `Setup Wizard`, biểu tượng Desktop và tùy chọn khởi động cùng Windows:
1. Tải và cài đặt [Inno Setup 6](https://jrsoftware.org/isdl.php) (miễn phí).
2. Nhấp chuột phải vào file [installer/RemotePrintManager.iss](file:///d:/ĐỒ ÁN WEBSITE/GITHUB/PRINTEXE/installer/RemotePrintManager.iss) → Chọn **Compile**.
3. Bộ cài đặt `RemotePrintManager_Setup_v1.0.0.exe` sẽ được tạo ra tại thư mục `bin-installer/`.

---

## 8. Hướng Dẫn Triển Khai Toàn Diện Lên GitHub (GitHub Deployment & CI/CD)

### 8.1. Đẩy mã nguồn lên kho chứa GitHub
1. Mở trình duyệt, đăng nhập GitHub và nhấn **New repository** (đặt tên: `RemotePrintManager` hoặc `PRINTEXE`).
2. Mở PowerShell tại thư mục dự án và thực thi chuỗi lệnh:
   ```powershell
   # Khởi tạo hoặc kiểm tra remote
   git remote add origin https://github.com/<tai-khoan-cua-ban>/RemotePrintManager.git
   
   # Đổi tên nhánh chính thành master hoặc main
   git branch -M master
   
   # Đẩy mã nguồn lên GitHub
   git push -u origin master
   ```

---

### 8.2. Thiết lập GitHub Actions tự động build và release khi gắn Tag
Dự án đã tích hợp sẵn kịch bản CI/CD tại [.github/workflows/ci-cd.yml](file:///d:/ĐỒ ÁN WEBSITE/GITHUB/PRINTEXE/.github/workflows/ci-cd.yml).
Mỗi khi bạn muốn phát hành một phiên bản mới, chỉ cần gắn tag phiên bản trong git:

```powershell
# Gắn tag phiên bản mới
git tag v1.0.0

# Đẩy tag lên GitHub
git push origin v1.0.0
```
**Quá trình tự động diễn ra trên GitHub:**
1. Máy chủ đám mây của GitHub (GitHub Actions) tự động khởi chạy môi trường Windows Server.
2. Tải mã nguồn, cài đặt .NET 9 SDK.
3. Chạy toàn bộ bộ test kiểm thử tự động.
4. Chạy `build.ps1` đóng gói `RemotePrintManager.exe`, `RemotePrintManager_v1.0.0_Portable.zip`, `version.json`.
5. Tự động tạo một mục **Release** chính thức trên GitHub và đính kèm đầy đủ các tệp tải về!

---

### 8.3. Cấu hình tự động cập nhật qua GitHub Releases
Để phần mềm tự động tải các bản cập nhật trong tương lai trực tiếp từ GitHub Releases của bạn mà không cần duy trì web server riêng:
1. Trong file `build.ps1`, bạn cập nhật đường dẫn `downloadUrl`:
   ```powershell
   downloadUrl = "https://github.com/<tai-khoan>/RemotePrintManager/releases/latest/download/RemotePrintManager.exe"
   ```
2. Đặt link kiểm tra cập nhật trên ứng dụng:
   `https://raw.githubusercontent.com/<tai-khoan>/RemotePrintManager/master/publish/version.json`
3. Giờ đây, chỉ cần bạn đẩy tag mới lên GitHub, tất cả các máy tính tại công ty sẽ tự động phát hiện phiên bản mới và người dùng chỉ cần nhấn **"Cập nhật ngay"**!

---

## 9. Xử Lý Sự Cố Thường Gặp (Troubleshooting & FAQs)

| Sự Cố | Nguyên Nhân | Cách Khắc Phục |
| :--- | :--- | :--- |
| **Máy con báo `Lỗi kết nối Relay`** | Máy chủ chưa bật hoặc cổng 9090 bị chặn bởi Windows Firewall | Đảm bảo phần mềm Máy Chủ đang chạy. Nếu dùng VPS, kiểm tra mở port 9090 trong Security Group / UFW (`sudo ufw allow 9090`). |
| **Không tìm thấy máy in chia sẻ** | Máy in USB chưa cắm hoặc chưa bật nguồn | Kiểm tra dây cáp USB cắm vào Máy Chủ. Vào tab "Máy in" trên Máy Chủ, bấm "Quét lại máy in" và bấm "In trang kiểm tra". |
| **Lệnh in báo thành công nhưng máy in không ra giấy** | Hàng đợi Windows Spooler trên máy chủ bị treo bởi tài liệu cũ | Mở CMD với quyền Admin trên máy chủ và chạy: `net stop spooler && net start spooler`. |
| **Máy con bị từ chối kết nối** | Mã ghép đôi sai hoặc Máy chủ chưa duyệt | Kiểm tra lại mã `RPM-xxxx` trên tab Tổng quan của Máy Chủ. Quản trị viên vào tab "Thiết Bị Kết Nối" và bấm "Phê duyệt". |
| **Windows Defender hiện thông báo SmartScreen** | Ứng dụng mới biên dịch chưa mua chứng chỉ số Code Signing | Nhấn **More info (Xem thêm thông tin)** → Chọn **Run anyway (Vẫn chạy)**. Ứng dụng an toàn 100% không chứa mã độc. |

---

## 10. Mở Rộng Không Giới Hạn Máy Con & Quản Lý Đa Máy In (Enterprise Fleet Scaling)

### 10.1. Không giới hạn số lượng thiết bị trong mã nguồn
* **Kiến trúc mở:** Remote Print Manager không thiết lập bất kỳ giới hạn cứng nào (hardcode limit) về số lượng máy con kết nối. Hệ thống hỗ trợ mở rộng tự nhiên từ 3 máy con ban đầu lên 10, 20, 50, 100 hoặc hàng trăm máy tính văn phòng.
* **Tối ưu hóa UI Virtualization:** Danh sách máy con sử dụng công nghệ `VirtualizingStackPanel` của WPF. Bảng danh sách có thể hiển thị hàng nghìn máy tính mượt mà ở tốc độ 60 FPS, không gây giật lag hay chiếm dụng bộ nhớ RAM.

### 10.2. Tìm kiếm, lọc và phân nhóm phòng ban (Department Grouping)
* **Ô tìm kiếm tức thì:** Tìm kiếm máy con theo tên gợi nhớ (ví dụ: *KeToan-01*) hoặc mã định danh UUID.
* **Bộ lọc đa chiều:**
  - Lọc theo trạng thái: *Tất cả*, *Trực tuyến (Online)*, *Ngoại tuyến (Offline)*, *Chờ phê duyệt*.
  - Lọc theo phòng ban: *Kế toán*, *Kinh doanh*, *Hành chính*, *Kỹ thuật*, *Ban Giám Đốc*, *Chung*.
* **Đổi phòng ban 1-Click:** Quản trị viên chỉ cần chọn máy con và bấm **"🏢 Đổi phòng ban"** để phân nhóm máy tính nhanh chóng.

### 10.3. Quản lý chia sẻ nhiều máy in USB & Phân quyền truy cập (Printer Access Control)
* **Chia sẻ nhiều máy in đồng thời:** Máy chủ có thể kết nối nhiều máy in qua cổng USB hoặc cổng mạng nội bộ. Tại tab *Máy In*, quản trị viên có thể bấm **"✔ Bật / Tắt chia sẻ máy in đã chọn"** cho từng máy in.
* **Phân quyền chi tiết (Access Control):**
  - Mặc định: Máy con được phép in trên toàn bộ máy in đang chia sẻ (`Tất cả máy in chia sẻ`).
  - Giới hạn quyền: Chọn máy con và bấm **"🖨️ Phân quyền máy in"** để chỉ định máy con đó chỉ được phép in trên máy in của phòng mình (ví dụ: nhân viên kinh doanh chỉ được in máy in màu, kế toán chỉ được in máy in hóa đơn).
  - Khi có lệnh in sai quyền, hệ thống tự động từ chối và gửi thông báo rõ ràng về máy con: *"Thiết bị của bạn không có quyền in trên máy in này"*.

### 10.4. Xử lý nhiều máy con in cùng lúc (Concurrent Print Queue)
* **Tiếp nhận đồng thời (Asynchronous Concurrency):** Khi 10-20 máy con đồng thời nhấn `Ctrl + P`, luồng WebSocket xử lý song song và đẩy vào hàng đợi an toàn được bảo vệ bởi `SemaphoreSlim`.
* **Thứ tự in FIFO & Chống in đè:** Máy in vật lý tiếp nhận tài liệu lần lượt theo đúng thứ tự đến trước in trước (FIFO), không bao giờ bị chèn trang hay nhầm lẫn văn bản giữa các nhân viên.
* **Cảnh báo tải cao:** Nếu hàng đợi vượt quá 20 lệnh in cùng lúc, hệ thống sẽ tự động ghi nhật ký cảnh báo và điều tiết tốc độ để bảo vệ máy in và máy chủ.

---

> **Bản quyền & Phát triển:** Remote Print Manager (RPM) © 2026. Thiết kế chuyên biệt cho môi trường doanh nghiệp và văn phòng chi nhánh.

