# Hướng dẫn sử dụng Remote Print Manager

Remote Print Manager giúp máy con gửi lệnh in đến máy chủ thông qua kết nối đã được ghép nối. Người dùng chỉ cần mở ứng dụng, chọn vai trò, ghép nối một lần và in bằng `Ctrl + P`.

Tài liệu này mô tả giao diện và quy trình sử dụng hiện tại. Hướng dẫn đóng gói, ký bản phát hành và vận hành kỹ thuật xem tại [DEPLOYMENT_AND_OPERATIONS.md](DEPLOYMENT_AND_OPERATIONS.md).

## 1. Yêu cầu hệ thống

- Windows 10 hoặc Windows 11 64-bit.
- Tệp phát hành `RemotePrintManager.exe`.
- Không cần cài .NET Runtime hoặc .NET SDK riêng khi dùng bản phát hành self-contained.
- Máy chủ phải có Windows Print Spooler, driver máy in và máy in hoạt động bình thường.
- Máy chủ và máy con cần có kết nối mạng phù hợp với cấu hình kết nối của bản phát hành.

> Không mở cổng dịch vụ nội bộ ra Internet công cộng. Không dùng `127.0.0.1` trên máy con để thay cho địa chỉ máy chủ từ xa.

## 2. Tải và chạy ứng dụng

1. Sao chép `RemotePrintManager.exe` vào máy cần sử dụng.
2. Mở tệp bằng quyền người dùng thông thường.
3. Ở lần chạy đầu tiên, chọn một vai trò:
   - **Máy chủ**: máy đang kết nối trực tiếp với máy in.
   - **Máy con**: máy gửi lệnh in đến máy chủ.
4. Nếu Windows hiển thị cảnh báo bảo mật, chỉ cho phép chạy tệp khi tệp được tải từ nguồn tin cậy.

## 3. Thiết lập Máy chủ

Máy chủ là máy có máy in vật lý được cài đặt trong Windows.

1. Mở `RemotePrintManager.exe` và chọn **Máy chủ**.
2. Mở tab **Máy in** trong ứng dụng.
3. Chọn máy in cần chia sẻ trong danh sách.
4. Khi chọn máy in, máy in đó được dùng làm máy in mặc định của Remote Print Manager. Không có nút **Mặc định** riêng.
5. Dùng công tắc bên cạnh máy in để bật hoặc tắt khả năng chia sẻ.
6. Lấy mã ghép nối hiển thị trên màn hình chính và gửi mã cho người dùng máy con.
7. Khi có yêu cầu mới, mở tab **Thiết bị**, chọn thiết bị cần cấp quyền và phê duyệt.

Nếu máy chủ chưa có máy in, hãy cài driver và kiểm tra máy in trong **Settings > Bluetooth & devices > Printers & scanners** của Windows trước khi mở lại ứng dụng.

## 4. Thiết lập Máy con

1. Mở cùng tệp `RemotePrintManager.exe` trên máy con.
2. Chọn **Máy con**.
3. Nhập mã ghép nối đang hiển thị trên máy chủ ở màn hình chính.
4. Gửi yêu cầu kết nối.
5. Chờ máy chủ phê duyệt thiết bị.
6. Khi trạng thái chuyển thành **Đã kết nối**, máy con có thể gửi lệnh in đến máy in được cấp quyền.

Mỗi máy con nên dùng một tên thiết bị dễ nhận biết. Không nhập `localhost`, `127.0.0.1` hoặc địa chỉ của chính máy con làm máy chủ từ xa.

## 5. In tài liệu hằng ngày

1. Mở tài liệu trong Word, Excel, trình duyệt, PDF hoặc ứng dụng cần in.
2. Nhấn `Ctrl + P`.
3. Chọn máy in đã được cấu hình cho Remote Print Manager.
4. Chọn số bản in, khổ giấy và các tùy chọn khác.
5. Nhấn **In**.

Lệnh in chỉ được chuyển đi khi thiết bị đã được ghép nối, được máy chủ phê duyệt và máy chủ đang trực tuyến.

## 6. Quản lý máy in

- Tab **Máy in** hiển thị danh sách máy in Windows mà ứng dụng nhận diện được.
- Chọn một máy in trong danh sách để thay đổi máy in đang sử dụng.
- Công tắc bật/tắt điều khiển trạng thái chia sẻ của từng máy in.
- Lựa chọn máy in được giữ lại khi chuyển qua lại giữa các tab.
- Nếu danh sách đang quét lại, chờ quá trình hoàn tất; lựa chọn hiện tại không bị xem là mất khi giao diện đang tải.

Các nhãn chính trong giao diện:

- **Thiết bị gửi**: thiết bị gửi lệnh in.
- **Máy in**: máy in nhận lệnh tại máy chủ.

## 7. Cài đặt chung

Màn hình **Cài đặt chung** chỉ hiển thị những thông tin người dùng cần:

- Tên thiết bị.
- Vai trò **Máy chủ** hoặc **Máy con**.
- Trạng thái kết nối thực tế.
- Phiên bản ứng dụng.
- Tùy chọn khởi động cùng Windows.

Địa chỉ IP, WebSocket, cổng kết nối và Feed URL không nằm trên màn hình chính. Khi cần kiểm tra kỹ thuật, mở **Chẩn đoán** hoặc **Cấu hình nâng cao**.

## 8. Trạng thái kết nối và ghép nối

- **Đã kết nối**: ứng dụng đã có bằng chứng kết nối hợp lệ với máy chủ.
- **Chưa kết nối**: chưa xác định được máy chủ hoặc kết nối hiện tại chưa được xác minh.
- **Đang chờ phê duyệt**: máy con đã gửi yêu cầu nhưng máy chủ chưa cấp quyền.

Nếu thấy **Chưa kết nối**:

1. Kiểm tra máy chủ đang mở ứng dụng.
2. Kiểm tra mã ghép nối còn đúng và chưa hết hiệu lực.
3. Kiểm tra máy chủ đã phê duyệt thiết bị trong tab **Thiết bị**.
4. Kiểm tra kết nối mạng và cấu hình endpoint trong phần **Chẩn đoán**.
5. Thử ghép nối lại sau khi đổi mạng hoặc khởi động lại ứng dụng.

Ứng dụng không coi kết nối là thành công chỉ vì đã có một địa chỉ localhost. Máy con phải sử dụng endpoint máy chủ có thể truy cập thực tế.

## 9. Cập nhật ứng dụng

1. Mở **Cài đặt**.
2. Tại dòng **Cập nhật**, xem phiên bản và trạng thái hiện tại.
3. Nhấn **Kiểm tra cập nhật**.
4. Nếu có bản mới, đọc nội dung chi tiết trong hộp thoại cập nhật rồi thực hiện cập nhật theo hướng dẫn.

Bản cập nhật được kiểm tra theo manifest và chữ ký của bản phát hành. Cấu hình người dùng được lưu ngoài thư mục ứng dụng nên không bị thay thế khi cập nhật.

Không tắt ứng dụng hoặc Windows trong khi đang thay thế bản phát hành.

## 10. Chẩn đoán

Chỉ dùng **Chẩn đoán** khi cần hỗ trợ kỹ thuật. Khu vực này có thể hiển thị:

- Endpoint kết nối đang được sử dụng.
- Địa chỉ mạng riêng và cổng liên quan.
- Trạng thái WebSocket hoặc kết nối hiện tại.
- Thông tin máy và phiên bản để đối chiếu lỗi.

Không chia sẻ công khai mã ghép nối, địa chỉ nội bộ hoặc thông tin chẩn đoán nếu không cần thiết.

## 11. Vị trí dữ liệu cấu hình

- Cấu hình người dùng: `%APPDATA%\\RemotePrintManager\\config.json`
- Dữ liệu hỗ trợ cập nhật: `%APPDATA%\\RemotePrintManager\\Updates`

Không xóa các thư mục này nếu muốn giữ vai trò, ghép nối và thiết lập máy in hiện tại. Khi đổi bản phát hành, dữ liệu cấu hình được giữ độc lập với tệp EXE.

## 12. Xử lý sự cố nhanh

### Không thấy máy in

Kiểm tra máy in đã được cài trong Windows, driver hoạt động và Windows Print Spooler đang chạy. Sau đó mở lại tab **Máy in**.

### Máy con chưa kết nối

Kiểm tra mã ghép nối, trạng thái máy chủ, phê duyệt trong tab **Thiết bị** và endpoint trong **Chẩn đoán**. Không dùng localhost của máy con.

### Lệnh in không ra giấy

Kiểm tra máy chủ đang mở, máy in đang bật chia sẻ, máy in còn giấy/mực và không bị Windows tạm dừng. Kiểm tra lại hàng đợi in trên máy chủ.

### Cần hỗ trợ

Khi báo lỗi, gửi phiên bản ứng dụng, vai trò của thiết bị, trạng thái hiển thị và thông tin chẩn đoán cần thiết. Không gửi mã ghép nối hoặc khóa bí mật lên nơi công khai.

Kho mã nguồn: [github.com/hnam1204/PRINT_EXE](https://github.com/hnam1204/PRINT_EXE)
