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
