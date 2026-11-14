# Sổ Tay Hướng Dẫn Sử Dụng Chi Tiết - Remote Print Manager (RPM)

## I. Giới Thiệu
Phần mềm **Remote Print Manager (RPM)** cho phép doanh nghiệp chia sẻ 1 máy in vật lý (cắm dây USB tại máy tính chủ) cho 3 máy tính con đặt tại các phòng ban hoặc văn phòng chi nhánh trong phạm vi bán kính 1 km, hoạt động trên các đường mạng Internet hoàn toàn độc lập mà không cần can thiệp cấu hình Router/Modem.

---

## II. Quy Trình Cài Đặt Ban Đầu

### 1. Phân Phối File Cài Đặt
Copy file `RemotePrintManager.exe` (hoặc giải nén từ file `RemotePrintManager_v1.0.0_Portable.zip` trong thư mục `publish/`) sang cả 4 máy tính:
- 01 Máy chủ (Server).
- 03 Máy con (Clients).

### 2. Thiết Lập Trên Máy Chủ (Server)
1. Mở phần mềm `RemotePrintManager.exe`.
2. Tại màn hình đầu tiên, bấm nút **"Thiết Lập Làm Máy Chủ →"**.
3. Phần mềm mở giao diện Quản Trị Máy Chủ với 5 tab:
   - **Tab 2 (Máy in):** Bấm "Quét máy in trên máy tính". Hệ thống sẽ liệt kê các máy in trên Windows. Chọn máy in USB của công ty (ví dụ: Canon LBP 2900, HP LaserJet...) và nhấn **"Chọn làm máy in chia sẻ"**. Bạn có thể bấm **"In trang kiểm tra"** để đảm bảo máy in hoạt động tốt.
   - **Tab 1 (Tổng quan):** Quan sát thẻ thông tin ghép đôi. Tại đây hiển thị một mã gồm 6 ký tự dạng `RPM-XXXX` (ví dụ: `RPM-8821`). Nhấn **"Sao chép mã"** và gửi mã này cho nhân viên ở 3 máy con.
   - **Tab 3 (Thiết bị kết nối):** Khi máy con gửi yêu cầu, danh sách sẽ hiện tên máy con kèm trạng thái "Chờ phê duyệt". Quản trị viên chỉ cần chọn máy con và bấm **"Phê duyệt (Approve)"**.

### 3. Thiết Lập Trên 3 Máy Con (Clients)
1. Mở phần mềm `RemotePrintManager.exe` trên máy con.
2. Tại màn hình đầu tiên, bấm nút **"Thiết Lập Làm Máy Con →"**.
3. Phần mềm mở giao diện Máy Con:
   - **Tab 2 (Tìm & Kết nối Máy chủ):** Nhập mã `RPM-XXXX` nhận được từ Máy Chủ và nhấn **"Gửi Yêu Cầu Ghép Đôi Tới Máy Chủ"**.
   - Chờ máy chủ bấm duyệt. Ngay khi được duyệt, trạng thái ở góc trên sẽ chuyển sang màu xanh: **"Đã kết nối & Phê duyệt thành công"**.
   - **Tab 3 (Máy in của tôi):** Nhấn nút **"Cài Đặt Máy In Ảo Vào Windows"**. Hệ thống sẽ tự động tạo cổng máy in IPP cục bộ và đăng ký máy in vào Windows Devices & Printers.

---

## III. Hướng Dẫn In Ấn Hàng Ngày (Nhân Viên Văn Phòng)

Sau khi cài đặt xong bước trên, nhân viên **hoàn toàn không cần thao tác gì thêm trên giao diện phần mềm**:

1. Mở văn bản Word, bảng tính Excel, hóa đơn PDF hoặc trang web bất kỳ.
2. Nhấn tổ hợp phím quen thuộc: **Ctrl + P**.
3. Trong danh sách máy in của Windows, chọn tên máy in công ty (ví dụ: `Canon LBP 2900 (Remote)`).
4. Tùy chỉnh số lượng bản in (Copies), khổ giấy (A4), in tất cả trang hoặc trang chỉ định, sau đó nhấn **Print (In)**.
5. Phần mềm chạy ngầm dưới góc phải màn hình sẽ tự động bắt luồng in, mã hóa AES-256 và truyền tải qua Internet đến máy chủ. Máy in USB sẽ xuất tài liệu ngay lập tức.
6. Khi in xong, góc màn hình máy con sẽ hiển thị thông báo bong bóng: *"Đã xuất tài liệu ra máy in thành công!"*.

---

## IV. Cơ Chế Bảo Mật & Ổn Định

1. **Mã hóa đầu cuối (E2EE):** Toàn bộ file in đều được mã hóa bằng thuật toán AES-256-GCM. Không một thiết bị trung gian nào trên đường truyền Internet có thể giải mã được nội dung tài liệu của công ty.
2. **Chống in trùng:** Hệ thống tự động tính toán mã băm SHA-256 của từng file in. Nếu có lệnh in trùng lặp trong thời gian ngắn do bấm nhầm hoặc rớt mạng kết nối lại, hệ thống sẽ tự động lọc và cảnh báo, tránh lãng phí giấy mực.
3. **Mất mạng & Khôi phục:** Khi máy chủ mất mạng hoặc máy in tắt nguồn, máy con sẽ nhận thông báo cảnh báo rõ ràng. Khi có mạng trở lại, hệ thống tự động kết nối lại mà không yêu cầu người dùng nhập lại mã.
4. **Phân quyền chặt chẽ:** Máy chủ có toàn quyền **Tạm dừng** hoặc **Thu hồi quyền in (Revoke)** của bất kỳ máy con nào chỉ với 1 click.

---

## V. Quy Trình Cập Nhật & Nâng Cấp Phần Mềm Trực Tiếp

### 1. Dành Cho Nhà Phát Triển (Tạo & Phát Hành Phiên Bản Mới)
Khi có cải tiến mã nguồn hoặc sửa lỗi (ví dụ nâng cấp lên **V1.0.1**):
1. Chỉnh sửa mã nguồn trong dự án và chạy kịch bản đóng gói:
   ```powershell
   .\build.ps1 -Version "1.0.1" -Changelog @(
       "Tối ưu tốc độ truyền tải tài liệu",
       "Tự động kết nối lại máy chủ khi mạng Internet phục hồi",
       "Khắc phục sự cố nhận diện máy in USB"
   )
   ```
2. Hệ thống sẽ tự động:
   - Chạy 8 bộ test kiểm thử tự động.
   - Biên dịch phiên bản `RemotePrintManager.exe` mới.
   - Tính toán mã băm kiểm tra tính toàn vẹn **SHA-256**.
   - Tạo file `publish/version.json` chứa thông tin phiên bản và liên kết tải.
3. Đẩy file `RemotePrintManager.exe` và `version.json` lên máy chủ Relay hoặc GitHub Releases/Web Server.

### 2. Dành Cho Người Dùng (Cập Nhật 1-Click Trên Máy Chủ & Máy Con)
1. Mở phần mềm, vào tab **Cài đặt**.
2. Tại khu vực **"Cập Nhật & Nâng Cấp Phần Mềm"**:
   - Nếu phần mềm bật tính năng tự động kiểm tra, badge sẽ hiển thị: **"Có bản mới V1.0.1"**.
   - Hoặc người dùng bấm nút **"🔍 Kiểm tra cập nhật"**.
   - Bấm **"📝 Xem nội dung thay đổi"** để đọc chi tiết các tính năng mới và lỗi đã khắc phục.
3. Nhấn nút **"⚡ Cập nhật ngay"**:
   - Phần mềm kiểm tra nếu đang có lệnh in dở dang, sẽ hỏi xác nhận để không làm hỏng tài liệu đang in.
   - Hiển thị thanh tiến trình tải xuống (0% → 100%).
   - Tự động kiểm tra mã băm SHA-256 để đảm bảo file tải về không bị lỗi hay can thiệp.
   - Tự động thay thế tệp thực thi, tạo bản sao lưu an toàn (`.bak`) và khởi động lại phần mềm.

### 3. Quy Trình Cập Nhật Thử Nghiệm (Canary Testing)
- Để đảm bảo an toàn tuyệt đối, người quản trị có thể chỉ bấm **Cập nhật ngay** trên **Máy con số 1** trước.
- Sau khi máy con số 1 nâng cấp lên V1.0.1 thành công và in thử nghiệm hoàn hảo, các máy còn lại (máy chủ và 2 máy con khác) mới thực hiện bấm nâng cấp.
- **Bảo toàn dữ liệu 100%:** Sau khi nâng cấp, toàn bộ vai trò (Server/Client), mã ghép đôi, máy in đã chia sẻ, danh sách máy con đã duyệt và lịch sử in ấn được giữ nguyên vẹn hoàn toàn trong `%AppData%\RemotePrintManager\config.json`.

