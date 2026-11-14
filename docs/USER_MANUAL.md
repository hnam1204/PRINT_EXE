# Sổ Tay Hướng Dẫn Sử Dụng & Vận Hành - Remote Print Manager (RPM)

Chào mừng bạn đến với **Remote Print Manager (RPM)** — giải pháp chia sẻ máy in qua Internet độc lập dành cho văn phòng chi nhánh, các phòng ban trong bán kính 1 km.

> [!NOTE]
> Để xem hướng dẫn triển khai máy chủ đám mây, đóng gói bộ cài và thiết lập GitHub CI/CD chi tiết, vui lòng xem tài liệu: [DEPLOYMENT_AND_OPERATIONS.md](file:///d:/ĐỒ ÁN WEBSITE/GITHUB/PRINTEXE/docs/DEPLOYMENT_AND_OPERATIONS.md).

---

## I. Giới Thiệu Chung

Phần mềm **Remote Print Manager (RPM)** được phát triển nhằm giải quyết triệt để bài toán:
* Công ty có **01 máy in** (cắm cáp USB vào máy tính chủ).
* Có **03 máy tính con** ở các phòng ban, tầng khác nhau hoặc địa điểm cách máy chủ tối đa 1 km.
* Mỗi máy tính sử dụng **đường truyền Internet độc lập** (không chung Wi-Fi, không chung mạng LAN nội bộ, dùng 4G hoặc nhà mạng khác nhau).
* **Mục tiêu:** Cả 3 máy con đều có thể in trực tiếp bằng lệnh `Ctrl + P` quen thuộc trong Word, Excel, Chrome, PDF... như một máy in nội bộ thông thường mà không cần kéo dây cáp hay cấu hình router phức tạp.

---

## II. Hướng Dẫn Tải Về & Khởi Chạy

### 1. Tải ứng dụng
* Tải tệp thực thi duy nhất: `RemotePrintManager.exe` (hoặc giải nén gói `RemotePrintManager_v1.0.0_Portable.zip`).
* Sao chép tệp này vào cả 4 máy tính (1 máy chủ và 3 máy con).

### 2. Yêu cầu hệ thống
* Hệ điều hành: Windows 10 hoặc Windows 11 (64-bit).
* Quyền hạn: Chạy với quyền người dùng thông thường hoặc Administrator khi cài máy in ảo lần đầu.

---

## III. Thiết Lập Trên Máy Chủ (Server)

Máy chủ là máy tính đang cắm dây cáp USB trực tiếp vào máy in công ty.

1. **Khởi chạy ứng dụng:** Mở `RemotePrintManager.exe`.
2. **Chọn chế độ:** Tại màn hình khởi động lần đầu, nhấn chọn **🖥️ THIẾT LẬP LÀM MÁY CHỦ (SERVER)**.
3. **Chọn máy in chia sẻ:**
   - Chuyển sang tab **"Máy In"**.
   - Bấm **"Quét máy in trên máy tính"**.
   - Bấm chọn dòng máy in USB của bạn (ví dụ: *Canon LBP 2900*, *HP LaserJet*...) và nhấn nút màu xanh: **"Chọn làm máy in chia sẻ"**.
   - *(Tùy chọn)*: Nhấn **"In trang kiểm tra"** để đảm bảo máy in đang hoạt động tốt.
4. **Lấy mã ghép đôi (Pairing Code):**
   - Chuyển sang tab **"Tổng Quan"**.
   - Tại khung màu xanh *Thông Tin Ghép Đôi*, sao chép mã ghép đôi gồm 8 ký tự (ví dụ: `RPM-8197`).
   - Cung cấp mã này cho 3 máy con.
5. **Duyệt máy con kết nối:**
   - Khi máy con gửi yêu cầu, bạn chuyển sang tab **"Thiết Bị Kết Nối"**.
   - Chọn máy con trong danh sách và bấm nút **"Phê duyệt (Approve)"**.

---

## IV. Thiết Lập Trên 3 Máy Con (Clients)

Thực hiện lần lượt trên từng máy con:

1. **Khởi chạy ứng dụng:** Mở `RemotePrintManager.exe`.
2. **Chọn chế độ:** Tại màn hình khởi động lần đầu, nhấn chọn **💻 THIẾT LẬP LÀM MÁY CON (CLIENT)**.
3. **Gửi yêu cầu ghép đôi:**
   - Chuyển sang tab **"Tìm & Kết Nối Máy Chủ"**.
   - Nhập mã ghép đôi nhận từ máy chủ (ví dụ: `RPM-8197`).
   - Nhập tên gợi nhớ máy tính của bạn (ví dụ: *Phong-Ke-Toan-01*).
   - Nhấn **"Gửi Yêu Cầu Ghép Đôi Tới Máy Chủ"**.
4. **Cài đặt máy in vào Windows:**
   - Sau khi Máy Chủ bấm duyệt, đèn tín hiệu trên góc màn hình sẽ chuyển sang xanh lá: `● Đã kết nối & Phê duyệt thành công`.
   - Chuyển sang tab **"Máy In Của Tôi"**, nhấn nút **"Cài Đặt Máy In Ảo Vào Windows"**.
   - Windows sẽ tự động đăng ký máy in `Remote Printer (RPM)` vào danh sách thiết bị.

---

## V. Hướng Dẫn In Tài Liệu Hàng Ngày

Sau khi hoàn tất bước cài đặt, nhân viên văn phòng **hoàn toàn không cần mở phần mềm Remote Print Manager mỗi khi in**:

1. Mở bất kỳ tài liệu nào bạn đang làm việc: Microsoft Word, Excel, file PDF, trình duyệt Web, phần mềm kế toán...
2. Nhấn tổ hợp phím: **`Ctrl + P`** (hoặc chọn Menu *File → Print*).
3. Trong danh sách máy in của Windows, chọn: **`Remote Printer (RPM)`**.
4. Chọn các thông số in mong muốn:
   - Số bản in (Number of copies)
   - Khổ giấy (A4, Letter)
   - Hướng trang (Dọc / Ngang)
   - Chọn in tất cả trang hoặc trang tùy ý
5. Nhấn nút **Print (In)**.
6. Lệnh in sẽ được truyền tức thì qua Internet đến máy in USB tại máy chủ và in ra giấy ngay lập tức.
7. Khi in xong, góc dưới màn hình sẽ hiển thị thông báo popup: *"Đã in tài liệu thành công"*.

---

## VI. Hướng Dẫn Cập Nhật & Nâng Cấp Phần Mềm (1-Click)

Khi đội ngũ phát triển phát hành phiên bản mới (ví dụ V1.0.1):
1. Vào tab **"Cài Đặt"** trên phần mềm (cả Máy Chủ và Máy Con).
2. Tại khu vực **"Cập Nhật & Nâng Cấp Phần Mềm"**:
   - Nhấn **"🔍 Kiểm tra cập nhật"** (hoặc phần mềm sẽ tự thông báo khi có bản mới).
   - Nhấn **"📝 Xem nội dung thay đổi"** để xem các tính năng mới và bản sửa lỗi.
3. Nhấn nút **"⚡ Cập nhật ngay"**:
   - Phần mềm tự động tải phiên bản mới và kiểm tra mã băm SHA-256 an toàn.
   - Quá trình cập nhật tự động hoàn tất và khởi động lại phần mềm.
   - **Bảo toàn 100% dữ liệu:** Toàn bộ vai trò, thông tin kết nối, máy in chia sẻ và lịch sử hoạt động đều được giữ nguyên vẹn.

---

## VII. Xử Lý Sự Cố Nhanh

* **Máy con báo mất kết nối:** Kiểm tra kết nối Internet trên máy con và đảm bảo máy chủ đang bật phần mềm.
* **Máy in không ra giấy:** Kiểm tra xem máy in USB đã cắm chắc chắn vào máy chủ chưa, máy in có còn giấy và mực không.
* **Cần hỗ trợ kỹ thuật:** Vui lòng liên hệ bộ phận IT nội bộ hoặc tạo Issue trên kho lưu trữ GitHub của dự án.
