# 07 — Khả năng gửi thư (deliverability)

Tài liệu cho người vận hành BlueMail: thư gửi ra ngoài đi thế nào, cần cấu hình gì để thư vào Hộp thư đến của Gmail,
Outlook, Yahoo… và xử lý khi có sự cố. Thiết kế chi tiết: `docs/00` D12, D26–D29; API: `docs/05` (Phase 12).

## 1. Đường đi của một thư gửi ra ngoài

1. Người dùng bấm Gửi (web) hoặc nộp qua SMTP 587/465 → `OutgoingDispatcher` / `RawSubmission` kiểm quyền dùng địa chỉ
   From, chính sách tổ chức, **khoá gửi do lạm dụng** (mục 6).
2. Thư được ký **DKIM** bằng khoá của tên miền From (2048 bit, `rsa-sha256`), lưu bản Đã gửi, rồi vào hàng đợi
   `mta.outbound_messages` / `mta.outbound_recipients` — mỗi người nhận một dòng, một trạng thái.
3. `BlueMail.Outbound` (`DeliveryWorker`) lấy việc bằng `FOR UPDATE SKIP LOCKED` (nhiều tiến trình chạy song song an
   toàn), nhóm theo tên miền đích, rồi:
   - chọn **IP gửi** theo dải của tổ chức (mục 3) — chưa tới trần làm ấm, PTR khớp HELO;
   - tra MX (RFC 5321 §5, MX rỗng RFC 7505 → trả lại ngay);
   - kiểm **DNSSEC** của MX và **TLSA** (DANE, RFC 7672); không có DANE thì xét **MTA-STS** (RFC 8461);
   - gửi với `MAIL FROM` là địa chỉ **VERP** `bounces+<mã thư>.<mã kiểm>@<tên miền người gửi>`;
   - ghi kết quả từng người nhận, ghi `admin.mail_log`, ghi kết quả TLS cho TLS-RPT.
4. Lỗi tạm (`4xx`, mất kết nối, DNS tạm lỗi, TLSA không tra được): thử lại 5 ph, 15 ph, 30 ph, 1 g, 2 g, 4 g… tới
   **5 ngày**; sau 4 giờ người gửi nhận thư báo trễ; hết hạn thì DSN (RFC 3464, tiếng Anh + ngôn ngữ người gửi).
   Lỗi vĩnh viễn (`5xx`) → DSN ngay; `5.1.x` (không có người nhận) → địa chỉ vào **danh sách chặn gửi** 90 ngày.

## 2. DNS bắt buộc cho mỗi tên miền khách hàng

Trang Quản trị → Tên miền hiển thị đúng các bản ghi cần tạo (`DomainRecordPlanner`) và kiểm lại định kỳ:

| Bản ghi | Giá trị | Ghi chú |
|---|---|---|
| MX | `10 mx.<nền tảng>.` | Nhận thư, nhận cả thư trả lại muộn (VERP) và báo cáo |
| SPF | `v=spf1 include:_spf.<nền tảng> -all` | `_spf.<nền tảng>` liệt kê mọi IP gửi; VERP dùng tên miền người gửi nên SPF **căn chỉnh** cho DMARC |
| DKIM | `bm<năm>._domainkey` TXT | Khoá tạo tự động, xoay vòng từ trang Quản trị |
| DMARC | `_dmarc` TXT `v=DMARC1; p=quarantine; rua=…` | Bắt đầu `p=none` vài tuần, xem báo cáo rồi siết dần |
| MTA-STS | `_mta-sts` TXT + `https://mta-sts.<miền>/.well-known/mta-sts.txt` | Bên gửi khác buộc TLS khi gửi tới ta |
| TLS-RPT | `_smtp._tls` TXT `v=TLSRPTv1; rua=mailto:…` | Nhận báo cáo TLS của bên khác |

Với IP gửi: **PTR** của mỗi IP phải trả về đúng tên HELO của IP đó, và tên đó phải trỏ (A/AAAA) lại đúng IP
(FCrDNS). `SendingIps` kiểm mỗi giờ; IP không khớp **không được dùng** (trang Quản trị → Hạ tầng gửi thư hiện "Không khớp").

## 3. Dải IP và làm ấm

- **Dải IP** (`mta.ip_pools`): `TRIAL` (tổ chức dùng thử — thư rủi ro cao, tách khỏi khách trả phí), `SHARED`,
  `DEDICATED` (IP riêng của một tổ chức). Đúng một dải là mặc định; tổ chức chưa gán dải dùng dải mặc định; nếu chưa
  cấu hình dải nào, thư đi từ địa chỉ của máy chủ với `BM_OUTBOUND_HELO`.
- Gán dải cho tổ chức: Quản trị nền tảng → Tổ chức (`PUT /api/v1/admin/tenants/{id}/ip-pool`).
- **Làm ấm IP mới** (`WarmupSchedule`): trần mỗi ngày UTC tăng dần 50 → 100 → 200 → 300 → 500 → 750 → 1 000 → … →
  700 000 trong 42 ngày, sau đó bỏ trần. Khi mọi IP của dải đã chạm trần, thư **chờ** (không tính là một lần thử,
  không bao giờ bị trả lại vì lý do này) và thử lại sau 30 phút; IP đã ấm được ưu tiên trước.
- Thêm IP mới: tạo PTR → thêm IP vào `_spf.<nền tảng>` (`scripts/dev-gen-zones.py` / DNS thật) → thêm vào dải với
  "Làm ấm địa chỉ này" → theo dõi tỉ lệ trả lại và Google Postmaster / Microsoft SNDS hằng ngày trong 6 tuần đầu.

## 4. Giới hạn theo đích và tự giảm tốc

`mta.destination_limits` (cấu hình được, mẫu cụ thể thắng `*`): số kết nối đồng thời và số thư/phút cho `gmail.com`,
`outlook.com`, `hotmail.com`, `yahoo.com`, mặc định `*`. Khi bên nhận trả `421` hoặc `4.7.x` kiểu "gửi quá nhanh"
(`Throttle.IsRateLimit`), cả tên miền đó tạm dừng 5 phút. Vượt số thư/phút thì thư chờ 30 giây (không tính lần thử).

## 5. TLS khi gửi: MTA-STS, DNSSEC và DANE, TLS-RPT

- **DANE trước, MTA-STS sau** (RFC 8461 §2). Với mỗi MX: nếu RRset MX, địa chỉ của MX và `_25._tcp.<mx>` TLSA đều
  được **DNSSEC xác thực** (`BlueMail.Dns/Dnssec.cs`, tự kiểm từ khoá gốc, không tin bit AD của máy phân giải), thì TLS
  là bắt buộc và chứng chỉ phải khớp một bản ghi DANE-TA(2)/DANE-EE(3). Bản ghi PKIX-TA/PKIX-EE bị bỏ qua (RFC 7672 §3.1.3).
- Tra TLSA ra **bogus** hoặc lỗi → không gửi tới MX đó lúc này (thử MX khác, rồi hoãn) — không bao giờ hạ xuống gửi
  văn bản trần. MX bogus → hoãn cả tên miền.
- Máy phân giải **không trả dữ liệu DNSSEC cho gốc** (ví dụ mạng nội bộ không ra Internet) → coi như "không có
  DNSSEC", DANE không áp dụng và log ghi rõ; khi đó chỉ MTA-STS bảo vệ. Cấu hình `BM_DNS_SERVERS` trỏ tới máy phân
  giải đệ quy chuyển tiếp được bản ghi DNSSEC (Unbound, BIND, 1.1.1.1 …). Tắt hẳn DANE: `BM_DANE=off`.
- **TLS-RPT** (RFC 8460): mỗi kết nối được đếm vào `mta.tls_results` (theo ngày, tên miền, chính sách
  `tlsa`/`sts`/`no-policy-found`, MX, IP; lỗi theo mã RFC 8460 §4.3: `starttls-not-supported`, `tlsa-invalid`,
  `dnssec-invalid`, `validation-failure`…). Giờ đầu mỗi ngày, báo cáo JSON nén gzip của hôm trước được gửi tới `rua`
  trong `_smtp._tls.<tên miền>` — qua thư (ký DKIM, tệp `<người gửi>!<miền>!<bắt đầu>!<kết thúc>!<id>.json.gz`) hoặc
  HTTPS POST. Trang Quản trị → Hạ tầng gửi thư → "TLS của kết nối gửi đi" cho xem tổng hợp.

## 6. Thư trả lại, khiếu nại, danh sách chặn gửi, tự khoá

- **Trả lại ngay** (bên nhận từ chối trong phiên): DSN cho người gửi; `5.1.x` → chặn gửi 90 ngày.
- **Trả lại muộn**: nhiều hệ thống nhận thư trước rồi mới báo lỗi. Thư báo lỗi đến địa chỉ VERP; MX chỉ nhận địa chỉ
  VERP có mã kiểm đúng (HMAC từ khoá chủ). `FeedbackProcessor` đọc báo cáo (RFC 3464), đánh dấu đúng người nhận là
  BOUNCED, ghi `mta.feedback_events`, chặn gửi khi `5.1.x`, và đưa nguyên thư báo lỗi vào Hộp thư đến người gửi.
- **Khiếu nại (ARF, RFC 5965)**: đăng ký vòng phản hồi (feedback loop) với các nhà cung cấp hỗ trợ (Yahoo CFL,
  Microsoft JMRP; Gmail không gửi ARF — xem Google Postmaster Tools) với địa chỉ `fbl@<nền tảng>`
  (`BM_FEEDBACK_ADDRESS`). Báo cáo `abuse`/`fraud`/`virus` → người nhận vào danh sách chặn gửi (vô thời hạn, quản trị
  viên gỡ được), cộng điểm cho người gửi. Thư được nhận ra bằng VERP hoặc Message-ID + người nhận gốc.
- **Tự khoá khi lạm dụng** (`SenderReputation`, `AbuseOptions`): trong 24 giờ, khi người dùng đã gửi ≥ 20 người nhận
  bên ngoài và **tỉ lệ trả lại cứng > 10 %**, hoặc có **≥ 3 khiếu nại và tỉ lệ > 0,3 %**, tài khoản ngừng gửi (web báo
  `sending_locked`, SMTP trả `550 5.7.1`) cho tới khi quản trị viên tổ chức cho gửi lại (Quản trị → Luồng thư →
  Tài khoản tạm ngừng gửi). Mỗi lần khoá/mở ghi nhật ký kiểm toán. Đây thường là dấu hiệu tài khoản bị chiếm: đổi
  mật khẩu, thu hồi phiên và mật khẩu ứng dụng, xem quy tắc chuyển tiếp trước khi mở.

## 7. Máy chủ chuyển tiếp (smarthost)

`BM_SMARTHOST=host:port` — mọi thư đi qua một máy chủ khác (bản cài riêng chưa có IP uy tín, hoặc mạng chặn cổng 25).
Khi dùng smarthost: không tra MX, không DANE/MTA-STS (máy chủ chuyển tiếp chịu trách nhiệm), VERP và DKIM vẫn giữ.

## 8. Chế độ bảo mật

Khi soạn thư, nút ổ khoá → chọn thời hạn (1/7/30/90/365 ngày) và có/không cần mã. Người nhận **bên ngoài** nhận thư
có liên kết riêng `https://<web>/c/<mã>` thay cho nội dung (thư báo vẫn ký DKIM, đi qua hàng đợi như thường); người
nhận trong cùng hệ thống nhận thư như bình thường. Trang xem không cần tài khoản; nếu cần mã, mã 6 số gửi tới chính địa
chỉ người nhận, hiệu lực 10 phút, tối đa 5 lần nhập sai, mỗi phút một mã. Người gửi thấy ai đã mở và có thể thu hồi /
cho xem lại ngay trong thư ở mục Đã gửi. Trang ghi rõ: **không ngăn được chụp màn hình hay chép lại**. Tệp đính kèm tải
qua liên kết ký, hết hạn sau 30 phút; ảnh từ máy chủ ngoài luôn bị chặn trên trang này.

## 9. Theo dõi hằng ngày

- Quản trị → Báo cáo / Sức khoẻ gửi thư: tỉ lệ giao thành công theo tên miền, tỉ lệ trả lại, khiếu nại, IP có nằm
  trong danh sách chặn công khai không (Spamhaus, Barracuda, SpamCop — lưu ý giấy phép, `docs/00`).
- Quản trị → Hạ tầng gửi thư: PTR, ngày làm ấm, số thư hôm nay của từng IP; lỗi TLS theo tên miền.
- Google Postmaster Tools, Microsoft SNDS cho từng tên miền/IP (cần đăng ký thủ công, ngoài BlueMail).

## 10. Xử lý sự cố

| Hiện tượng | Xem ở đâu | Thường do |
|---|---|---|
| Thư "đang chờ" lâu, số lần thử 0 | Hàng đợi; Hạ tầng gửi thư | Mọi IP của dải chạm trần làm ấm, hoặc PTR không khớp |
| `451 4.7.5 TLSA lookup failed` / `DNSSEC validation failed` | Tìm vết thư | Vùng của bên nhận ký sai hoặc hết hạn chữ ký — chờ họ sửa; xác nhận bằng `delv`/DNSViz |
| `tlsa-invalid` trong TLS-RPT | Hạ tầng gửi thư | Bên nhận thay chứng chỉ mà chưa cập nhật TLSA |
| Gmail trả `421 4.7.28` | Tìm vết thư | Gửi quá nhanh / uy tín IP thấp: giảm `destination_limits`, làm ấm chậm hơn |
| Người dùng báo "không gửi được", lỗi `sending_locked` | Luồng thư → Tài khoản tạm ngừng gửi | Tự khoá (mục 6) |
| Thư tới Gmail vào Spam | Tiêu đề Authentication-Results bên nhận | SPF/DKIM/DMARC chưa căn chỉnh; DMARC `p=none` lâu; nội dung |
