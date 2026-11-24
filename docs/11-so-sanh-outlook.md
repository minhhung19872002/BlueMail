# 11. So sánh với Outlook

Đối chiếu ngày 2026-11-24. Outlook dùng để so là Outlook 16 (Microsoft 365, Windows), hồ sơ thử nghiệm `BlueMail-test`
(minh@bluemail.test, IMAP/SMTP tới BlueMail dev). Danh sách lệnh lấy từ ribbon từng mô-đun bằng agent-desktop.
Giao diện web BlueMail làm theo Outlook trên web.

Ký hiệu: ✅ có · ⚠️ có một phần · ❌ chưa có · — không áp dụng.

## Thư

| Outlook | BlueMail | Ghi chú |
|---|---|---|
| Ngăn đọc bên phải / dưới / tắt | ✅ | Mặc định bên phải như Outlook trên web; mật độ hiển thị |
| Danh sách 3 dòng, ảnh đại diện, nhóm theo ngày | ✅ | Hôm nay / Hôm qua / Tuần này / Tuần trước / tháng… theo múi giờ người dùng |
| Focused / Other | ✅ | Thẻ Chính / Quảng cáo / Mạng xã hội / Cập nhật / Diễn đàn |
| Yêu thích ở đầu cột thư mục | ✅ | Ghim mọi thư mục/nhãn |
| Thanh ứng dụng Thư / Lịch / Danh bạ / Việc | ✅ | Trên điện thoại nằm trong menu |
| New Email, Reply, Reply All, Forward | ✅ | |
| Forward as attachment | ✅ | `.eml` (message/rfc822) |
| Delete, Archive, Move, Unread/Read | ✅ | Kèm "Xoá thư này", "Đánh dấu chưa đọc từ đây" |
| Junk (chặn người gửi, báo thư rác/lừa đảo) | ✅ | Kèm bảng "vì sao vào Thư rác" |
| Sweep | ✅ | Theo hội thoại; tuỳ chọn chặn, chỉ thư cũ hơn 10 ngày |
| Ignore conversation | ✅ | "Tắt tiếng" |
| Categorize | ✅ | Nhãn màu, lồng nhau |
| Rules | ✅ | Bộ lọc với ngôn ngữ tìm kiếm |
| Filter Email (Unread, Has attachments, Flagged…) | ⚠️ | Tất cả / Chưa đọc / Gắn sao / Có tệp; các lọc khác qua ô tìm kiếm |
| Follow Up (cờ có hạn: Hôm nay, Ngày mai, Tuần này…) | ⚠️ | Gắn sao = cờ (đồng bộ IMAP `\Flagged`); "Thêm vào Việc cần làm" đặt được hạn; chưa có cờ kèm hạn trên chính thư |
| Snooze, Schedule send, Undo send | ✅ | |
| Encrypt / không chuyển tiếp | ✅ | Chế độ bảo mật |
| Meeting invitation card (Yes / Maybe / No) | ✅ | iMIP, tự vào lịch |
| Message details (mailed-by, signed-by, TLS) | ✅ | |
| Search with scope, gợi ý khi gõ | ✅ | |
| Sort by From / Size / Subject | ❌ | Chỉ theo ngày (D30: danh sách lớn) |
| Quick Steps | ❌ | |
| Clean Up (xoá thư lặp trong hội thoại) | ❌ | |
| Reply with Meeting | ❌ | |
| Read Aloud, Zoom | — | Dùng tính năng của trình duyệt |
| Send to OneNote | — | |

## Lịch

| Outlook | BlueMail | Ghi chú |
|---|---|---|
| Ngày / Tuần làm việc / Tuần / Tháng | ✅ | Điện thoại mặc định Ngày |
| Today, Back/Forward, Go to Date | ✅ | |
| New Appointment / New Meeting (mời khách) | ✅ | Thư mời iMIP REQUEST; khách trả lời cập nhật trạng thái |
| Lặp lại (hằng ngày, ngày làm việc, tuần, tháng, năm) | ✅ | RRULE, EXDATE; xoá một lần hoặc cả chuỗi |
| Nhắc (reminder) | ✅ | Trong trang và thông báo máy tính khi BlueMail đang mở |
| Huỷ cuộc họp (thông báo khách) | ✅ | iMIP CANCEL |
| Next 7 Days, Schedule view | ❌ | |
| Open/Share Calendar, Calendar Groups, Permissions, Publish Online | ❌ | Lịch dùng chung chưa có |
| E-mail Calendar | ❌ | |
| Teams / Skype meeting | — | Không có dịch vụ họp trực tuyến |

## Danh bạ

| Outlook | BlueMail | Ghi chú |
|---|---|---|
| New Contact, Contact Group, Delete | ✅ | |
| Address Book (danh bạ tổ chức) | ✅ | |
| Forward Contact (vCard), nhập/xuất | ✅ | vCard 2.1–4.0, CSV Google/Outlook |
| Gộp liên hệ trùng | ✅ | |
| Share Contacts, Open Shared Contacts | ❌ | |
| Mail Merge | ❌ | |
| Meeting / Follow Up từ liên hệ | ❌ | |

## Việc cần làm

| Outlook | BlueMail | Ghi chú |
|---|---|---|
| New Task, Delete, Mark Complete, Remove from List | ✅ | |
| To-Do List gồm thư gắn cờ | ✅ | Danh sách "Thư gắn cờ"; xong = bỏ sao |
| Hạn, Quan trọng, ghi chú | ✅ | Danh sách Quan trọng / Có hạn / Đã xong |
| Việc tạo từ thư, mở lại thư | ✅ | |
| Categorize task, giao việc (Assign/Reply/Forward task) | ❌ | |
| Nhắc cho việc | ❌ | |

## Việc nên làm tiếp (theo mức dùng thường gặp)

1. Cờ theo dõi có hạn ngay trên thư (Follow Up: Hôm nay / Ngày mai / Tuần này / Tuần sau / Tuỳ chọn), hiện trong Việc cần làm.
2. Sắp xếp danh sách thư theo người gửi / kích thước / tiêu đề (cần đường truy vấn riêng giữ được hiệu năng D30).
3. Quick Steps (chuỗi thao tác lưu sẵn) và Clean Up hội thoại.
4. Lịch dùng chung giữa người trong tổ chức (xem rảnh/bận, chia sẻ quyền).
