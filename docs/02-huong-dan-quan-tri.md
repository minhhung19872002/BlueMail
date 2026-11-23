# 02 — Hướng dẫn quản trị

Hai vai trò: **quản trị nền tảng** (mọi tổ chức, hạ tầng gửi thư) và **quản trị tổ chức** (người dùng, tên miền, chính
sách của tổ chức mình). Bảng điều khiển: avatar → Quản trị (`/admin/<mục>`); mỗi mục chỉ hiện với người có quyền, API
kiểm lại quyền. Mọi thay đổi ghi **nhật ký kiểm toán** (chuỗi băm, không sửa được).

## 1. Tổ chức (quản trị nền tảng)

Quản trị → Tổ chức: tạo tổ chức, gói, hạn mức lưu trữ và số người dùng, giới hạn gửi/ngày, dải IP gửi, tạm khoá (thư
đến được trả `450` để bên gửi thử lại, hoặc `550`). "Đăng nhập hỗ trợ" vào tài khoản người dùng cần lý do và để lại
dấu vết kiểm toán.

## 2. Tên miền

1. Quản trị → Tên miền → Thêm (ví dụ `congty.vn`).
2. Trình hướng dẫn hiện các bản ghi DNS phải tạo: TXT xác minh, MX, SPF (`include:_spf.<nền tảng>`), DKIM, DMARC,
   MTA-STS, TLS-RPT (`docs/04`, `docs/07`). Nút "Kiểm tra" tra lại; một việc nền kiểm định kỳ và báo khi bản ghi sai.
3. **Xoay khoá DKIM**: tạo khoá mới (selector mới), công bố DNS, rồi kích hoạt; khoá cũ giữ đến khi hết thư ký bằng nó.
4. Tên miền bí danh: thư tới `@bí-danh` vào cùng hộp thư.

## 3. Người dùng, nhóm, hộp thư dùng chung

- Thêm từng người hoặc **nhập CSV** (mẫu tải trong trang). Đặt mật khẩu tạm (buộc đổi lần đầu), hạn mức, đơn vị.
- Khoá tài khoản: mọi phiên web, IMAP, SMTP của người đó bị ngắt ngay.
- **Bí danh** người dùng (có cho phép gửi bằng bí danh không).
- **Nhóm** (danh sách thư): thành viên trong/ngoài tổ chức, ai được gửi tới nhóm, kiểm duyệt (thư chờ người duyệt).
- **Hộp thư dùng chung** (ví dụ `hotro@`): thành viên đọc/gửi theo quyền được cấp.
- **Tài khoản tạm ngừng gửi** (Luồng thư → Tài khoản tạm ngừng gửi): tài khoản tự bị khoá gửi khi tỉ lệ thư trả lại
  hoặc khiếu nại vượt ngưỡng (`docs/07` mục 6). Kiểm tra (đổi mật khẩu, phiên, chuyển tiếp lạ) rồi "Cho gửi lại".

## 4. Chính sách

Quản trị → Chính sách, theo tổ chức hoặc đơn vị: cỡ thư tối đa, loại tệp cấm, chân thư bắt buộc khi gửi ra ngoài,
người gửi luôn tin cậy / luôn chặn, bật/tắt IMAP, bắt buộc 2FA, chuyển tiếp ra ngoài, ngưỡng thư rác… Đổi chính sách
có tác dụng ngay (không cần khởi động lại). **Quy tắc định tuyến**: thư đến/đi khớp điều kiện → từ chối, cách ly,
chuyển hướng, gửi bản lưu (BCC).

## 5. Thư rác và cách ly

Quản trị → Thư rác và cách ly: ngưỡng điểm (vào Thư rác / từ chối trong phiên), danh sách DNSBL bật/tắt (chú ý giấy
phép, `docs/00`), luật phân loại tab; danh sách thư bị **cách ly** (nghi mã độc, luật định tuyến, nhóm kiểm duyệt):
xem tiêu đề, thả ra hoặc huỷ.

## 6. Luồng thư

- **Tìm vết thư**: theo người gửi, người nhận, Message-ID, thời gian — từng bước nhận/giao/từ chối, phản hồi SMTP.
- **Hàng đợi gửi**: thư đang chờ/hoãn, lý do; thử lại ngay hoặc huỷ.
- **Danh sách chặn gửi**: địa chỉ trả lại cứng hoặc khiếu nại; gỡ khi người nhận đã sửa hộp thư.
- **Sức khoẻ gửi thư**: tỉ lệ giao theo tên miền đích, trả lại, khiếu nại, IP có trong danh sách chặn công khai không.

## 7. Báo cáo

Thư nhận/gửi theo ngày, thư rác bị chặn, dung lượng theo người, đăng nhập; xuất Excel.

## 8. Tuân thủ

- **Lưu giữ**: giữ thư tối thiểu N ngày (người dùng xoá vẫn còn tới hạn) và/hoặc xoá thư cũ hơn N ngày.
- **Giữ pháp lý**: theo người, theo tìm kiếm, theo thời gian; thư bị giữ không bao giờ bị xoá hẳn, kể cả khi Thùng rác
  tự dọn.
- **eDiscovery**: tìm trên nhiều hộp thư, xuất kết quả (mbox + bảng kê), liên kết tải có hạn, ghi kiểm toán.
- **Chuyển dữ liệu**: nhập hàng loạt từ máy chủ IMAP cũ cho nhiều người.

## 9. Hạ tầng gửi thư (quản trị nền tảng)

Quản trị → Hạ tầng gửi thư: dải IP (dùng thử / dùng chung / riêng), thêm IP kèm tên HELO (phải trùng PTR), làm ấm IP
mới (giới hạn mỗi ngày tăng dần ~6 tuần), số thư đã gửi hôm nay; kết quả TLS (MTA-STS/DANE) theo tên miền nhận.
Chi tiết vận hành: `docs/07`.

## 10. Việc nền

Chạy trong tiến trình `worker`: trả thư tạm ẩn, nộp thư hẹn giờ, dọn Thùng rác/Thư rác 30 ngày (trừ giữ pháp lý),
kiểm DNS tên miền, áp lưu giữ, nhập/xuất, áp bộ lọc cho thư cũ, cập nhật danh sách mã độc. Báo cáo TLS-RPT hằng ngày
chạy trong `outbound`. Mọi việc dùng "thuê" có hạn trong CSDL: chạy nhiều bản song song an toàn.
