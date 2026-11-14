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
* Chỉ dùng `ws://127.0.0.1:9090` cho relay loopback nội bộ. Relay public bắt buộc đi qua TLS reverse proxy và dùng:
  `wss://relay.tencongty.com`.
* Không expose trực tiếp port HTTP/WebSocket của relay ra Internet; reverse proxy phải terminate TLS và forward vào relay nội bộ.

### Signed updates

* Release pipeline phải cung cấp secret `RPM_UPDATE_PRIVATE_KEY_PEM` và dùng `build.ps1 -DownloadBaseUrl https://relay.tencongty.com`.
* Client chỉ chấp nhận manifest có chữ ký RSA-PSS hợp lệ; SHA-256 chỉ dùng kiểm tra integrity, không thay thế chữ ký.

---

## 6. Quy Trình Cập Nhật & Nâng Cấp Phần Mềm Tự Động & Trực Tiếp (Update & Upgrade)

Tính năng cập nhật trực tiếp cho phép nâng cấp phiên bản mới mà **không cần đến từng máy tính để cài đặt lại thủ công hay cấu hình lại từ đầu**.

### 6.1. Thao tác trên giao diện người dùng (Cả Máy Chủ và Máy Con)
1. Vào tab **"Cài đặt"** → Khu vực **"Cập nhật & Nâng cấp phần mềm"**.
2. Phần mềm hỗ trợ 2 chế độ:
   * **Cập nhật thủ công:** Người dùng chủ động nhấn nút **"🔍 Kiểm tra cập nhật"** bất kỳ lúc nào.
   * **Cập nhật tự động:** Phần mềm tự động kiểm tra khi khởi động và định kỳ mỗi 60 phút. Khi phát hiện bản mới, xuất hiện thông báo:
     > *"Đã có phiên bản Remote Print Manager V1.0.1. Phiên bản mới cải thiện khả năng kết nối và khắc phục lỗi hoạt động. Bạn có muốn cập nhật ngay không?"*
3. Khi có bản cập nhật mới, nút **"⚡ Cập nhật & Nâng cấp"** nổi bật bằng màu cam thương hiệu (`#FF7900`).
4. Bấm nút **"⚡ Cập nhật & Nâng cấp"**:
   * Hệ thống tự động kiểm tra: nếu đang có tài liệu in dở dang, phần mềm cảnh báo ưu tiên in xong trước để tránh gián đoạn bản in.
   * Tiến trình tải xuống hiển thị trực quan (0% - 100%) kèm thanh đo màu cam.
   * Hệ thống tự động xác thực mã băm SHA-256 để chống lỗi tải file hoặc tệp bị can thiệp.
   * Tự động sao lưu bản cũ thành `RemotePrintManager.exe.bak`, hoán đổi tệp mới và khởi động lại.
5. Nếu phiên bản đang sử dụng là mới nhất, hệ thống hiển thị: **"Bạn đang sử dụng phiên bản mới nhất"**.

---

### 6.2. Kênh phát hành thử nghiệm (Release Channels: Stable & Beta)
Để hỗ trợ thử nghiệm phiên bản mới trên một máy trước khi áp dụng cho toàn bộ văn phòng:
* **Kênh Chính thức (Stable):** Mặc định cho Máy Chủ và các máy con cần độ ổn định tuyệt đối. Chỉ nhận những bản phát hành chính thức đã qua kiểm nghiệm.
* **Kênh Thử nghiệm (Beta):** Dành cho máy con thử nghiệm (ví dụ Máy con số 1).
  * Khi Developer tạo bản vá lỗi (ví dụ V1.0.2 với kênh Beta), chỉ máy con số 1 nhận bản cập nhật này.
  * Máy chủ và các máy con khác vẫn giữ nguyên phiên bản ổn định hiện tại.
  * Sau khi máy con 1 in thử thành công, Developer phát hành bản V1.0.2 sang kênh Stable để toàn bộ công ty cùng nâng cấp.

---

### 6.3. Cơ chế Khôi phục phiên bản trước (Rollback) & Bảo toàn dữ liệu 100%
* **Bảo toàn dữ liệu 100%:** Toàn bộ thông tin cấu hình đặt tại `%AppData%\RemotePrintManager\config.json` tách biệt với tệp thực thi. Sau khi nâng cấp, phần mềm tự nhận diện lại vai trò máy chủ/máy con, mã ghép đôi, máy in chia sẻ, danh sách máy con được duyệt và toàn bộ lịch sử in ấn.
* **Khôi phục phiên bản trước (Rollback):** Nếu bản cập nhật mới gặp sự cố tương thích, giao diện sẽ xuất hiện nút **"↺ Khôi phục phiên bản trước"**. Chỉ cần 1 cú nhấp chuột, hệ thống sẽ khôi phục lại tệp `.bak` trước đó và khởi động lại ngay lập tức mà không làm mất cấu hình.

---

### 6.4. Quy trình phát hành bản cập nhật mới (Dành cho Developer)
Khi bạn chỉnh sửa mã nguồn để sửa lỗi hoặc bổ sung tính năng mới:
1. Mở PowerShell tại thư mục dự án và chạy công cụ phát hành tự động:
   ```powershell
   # Phát hành bản thử nghiệm cho máy con kiểm thử:
   .\scripts\publish_release.ps1 -Version "1.0.1" -Channel "Beta" -Changelog @("Khắc phục lỗi tự động kết nối lại sau khi mất Internet")

   # Hoặc phát hành bản chính thức cho toàn bộ công ty:
   .\scripts\publish_release.ps1 -Version "1.0.1" -Channel "Stable" -Changelog @("Bản phát hành chính thức ổn định")
   ```
2. Kịch bản sẽ tự động:
   * Chạy toàn bộ 17 bài Unit Test (bắt buộc đạt 100%).
   * Biên dịch Single-File Executable `RemotePrintManager.exe` mới.
   * Tính toán mã băm SHA-256 xác thực.
   * Tạo tệp manifest `publish/version.json` (hoặc `version_beta.json`).
   * Đồng bộ tệp tải về vào Relay Server hoặc GitHub Releases.
3. Khi Relay Server hoặc máy chủ GitHub tiếp nhận file mới, cả 4 máy tính đang chạy phần mềm sẽ tự động phát hiện và người dùng chỉ cần nhấn **"Cập nhật & Nâng cấp"** là hoàn tất!

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

## 11. Khả Năng Tương Thích Đa Thiết Bị Windows & Công Cụ Chẩn Đoán (System Diagnostics)

### 11.1. Hỗ trợ đa dạng phần cứng & thương hiệu máy tính
* **Thiết bị:** Tương thích 100% với Máy tính để bàn (Desktop PC), Máy tính xách tay (Laptop), Máy tính siêu nhỏ (Mini PC / Intel NUC), Máy trạm (Workstation), Máy All-in-One.
* **Thương hiệu:** Hoạt động độc lập và bình đẳng trên mọi hãng phần cứng (Dell, HP, Lenovo, ASUS, Acer, MSI, Gigabyte, tự lắp ráp...).
* **Kiến trúc:** Tối ưu hóa cho Windows x64 và ARM64 (qua lớp dịch x64 trên Windows 11).

### 11.2. Hỗ trợ các phiên bản hệ điều hành Windows
* **Windows 10 (64-bit):** Bản dựng (Build) từ 19041 (20H1/21H1/22H2) trở lên — Tương thích hoàn hảo với Máy Chủ công ty.
* **Windows 11 (64-bit):** Bản dựng từ 22000 (21H2/22H2/23H2/24H2) trở lên — Tương thích tốt với các máy tính văn phòng mới.
* **Windows Server:** Hỗ trợ Windows Server 2016, 2019, 2022 khi triển khai Relay hoặc vai trò Print Host.

### 11.3. Khả năng tương thích mọi thương hiệu máy in
* **Hãng máy in hỗ trợ:** Canon (LBP 2900, 3300, 6030, MF series...), HP (LaserJet Pro, Neverstop, Ink Tank...), Brother (HL series, DCP series...), Epson (EcoTank, L series...), Samsung, Ricoh, Xerox, Konica Minolta...
* **Giao tiếp:** Tối ưu hóa cổng USB kết nối trực tiếp vào Máy Chủ, hỗ trợ cả máy in kết nối qua cổng mạng nội bộ.

### 11.4. Tương thích 100% với các ứng dụng Windows (Ctrl + P)
Nhân viên không cần mở phần mềm Remote Print Manager, mà in trực tiếp từ:
* **Bộ ứng dụng văn phòng:** Microsoft Word, Microsoft Excel, PowerPoint, Outlook.
* **Trình duyệt Web:** Google Chrome, Microsoft Edge, Cốc Cốc, Firefox.
* **Trình đọc & in ấn PDF:** Adobe Acrobat Reader, Foxit PDF Editor, Nitro PDF.
* **Phần mềm doanh nghiệp:** Phần mềm kế toán MISA SME / AMIS, FAST, Bravo, phần mềm bán hàng KiotViet, Sapo...

### 11.5. Công cụ chẩn đoán hệ thống tự động (System Diagnostics Tool)
Phần mềm tích hợp sẵn công cụ chẩn đoán tương thích hệ thống:
* **Cách mở:** Bấm nút **"🔍 Chẩn đoán"** trên thanh tiêu đề hoặc vào tab **Cài Đặt** → **"🔍 Chẩn Đoán Thiết Bị"**.
* **Bảng kiểm tra 7 hạng mục tự động:**
  1. *Hệ điều hành Windows:* Nhận diện Windows 10/11 64-bit và số Build.
  2. *Kiến trúc CPU:* Kiểm tra hỗ trợ xử lý x64 đa nhân.
  3. *Quyền hạn thực thi:* Kiểm tra quyền Administrator vs Standard User.
  4. *Dịch vụ in ấn Windows Spooler:* Tự động phát hiện dịch vụ `spoolsv` đang chạy hay bị tắt, có nút **"🛠️ Khởi động lại Spooler"** tự sửa lỗi trong 1 click.
  5. *Nhận diện máy in & Driver:* Tự động quét và liệt kê các dòng máy in của Canon, HP, Brother, Epson...
  6. *Cổng mạng IPP (9199):* Kiểm tra tính sẵn sàng để mở luồng in ngầm.
  7. *Ứng dụng tương thích:* Xác thực khả năng in qua phím tắt `Ctrl + P`.

---

> **Bản quyền & Phát triển:** Remote Print Manager (RPM) © 2026. Thiết kế chuyên biệt cho môi trường doanh nghiệp và văn phòng chi nhánh.

