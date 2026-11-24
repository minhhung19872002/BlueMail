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
| Follow Up (cờ có hạn: Hôm nay, Ngày mai, Tuần này, Tuần sau, Không hạn) | ✅ | Gắn cờ (sao, đồng bộ IMAP `\Flagged`) và tạo việc có hạn liên kết thư; hạn tuần này/tuần sau là thứ Sáu như Outlook |
| Snooze, Schedule send, Undo send | ✅ | |
| Encrypt / không chuyển tiếp | ✅ | Chế độ bảo mật |
| Meeting invitation card (Yes / Maybe / No) | ✅ | iMIP, tự vào lịch |
| Message details (mailed-by, signed-by, TLS) | ✅ | |
| Search with scope, gợi ý khi gõ | ✅ | |
| Hộp thư dùng chung (mở, gửi với tư cách, gửi thay mặt) | ✅ | Chọn hộp thư ở đầu cột thư mục; From/Sender đúng RFC 5322 |
| Sort by From / Size / Subject | ✅ | Menu "Sắp xếp" trên danh sách; danh sách > 10.000 thư khớp vẫn theo ngày và báo rõ (D30) |
| Quick Steps | ✅ | Tạo/sửa trong "Quản lý Quick Steps…" (tối đa 20 bước, mỗi bước 5 thao tác có thể hoàn tác); chạy từ thanh công cụ thư |
| Clean Up (xoá thư lặp trong hội thoại) | ✅ | Menu "…" của hội thoại → Dọn dẹp hội thoại: thư được thư sau trích nguyên văn vào Thùng rác (hoàn tác được); giữ thư có tệp, nháp, thư rất ngắn |
| Reply with Meeting | ✅ | Menu "…" của hội thoại: mở sự kiện mới với tiêu đề hội thoại và mọi người trong hội thoại làm khách mời |
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
| Scheduling Assistant (rảnh/bận khi mời họp) | ✅ | Người trong tổ chức: rảnh / bận giờ này; ngoài tổ chức: không xem được lịch; chỉ thời gian, không lộ tiêu đề |
| Share Calendar, Open Calendar, Permissions | ✅ | Chia sẻ cho đồng nghiệp với quyền Được xem / Được sửa; hiện lịch của người khác chồng lên lịch mình; cuộc họp có khách chỉ người tổ chức sửa |
| Publish Online | ✅ | Đường dẫn ICS bí mật (chỉ giờ bận, hoặc đầy đủ trừ khách mời), tạo lại để thu hồi; iPhone/Google/Outlook đăng ký được |
| Calendar Groups | ❌ | |
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
| Mail Merge | ✅ | Từ một nhóm danh bạ: mỗi người một thư riêng ({{name}}, {{fullName}}, {{email}}), gửi qua đường gửi thường, tối đa 100 |
| Meeting / Follow Up từ liên hệ | ❌ | |

## Việc cần làm

| Outlook | BlueMail | Ghi chú |
|---|---|---|
| New Task, Delete, Mark Complete, Remove from List | ✅ | |
| To-Do List gồm thư gắn cờ | ✅ | Danh sách "Thư gắn cờ"; xong = bỏ sao |
| Hạn, Quan trọng, ghi chú | ✅ | Danh sách Quan trọng / Có hạn / Đã xong |
| Việc tạo từ thư, mở lại thư | ✅ | |
| Categorize task | ✅ | Nhóm màu (cùng bảng màu với Lịch) |
| Giao việc (Assign/Reply/Forward task) | ❌ | |
| Nhắc cho việc | ❌ | |

## Việc nên làm tiếp (theo mức dùng thường gặp)

1. Nhóm lịch (Calendar Groups).
