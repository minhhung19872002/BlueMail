# 00 — Quyết định kỹ thuật (Technical decisions)

Tài liệu kỹ thuật nội bộ. Mỗi quyết định ghi: **bối cảnh → quyết định → bằng chứng → hệ quả**.
Mục nào cần chủ sản phẩm xác nhận được đánh dấu **[CẦN CHỦ SẢN PHẨM]**.

Ngày lập: 2026-11-23 (Phase 0).

---

## D0. Đổi hướng đặc tả: tự viết máy chủ thư

Bản đặc tả đầu tiên dựa trên Stalwart Mail Server; bản hiện hành (PROMPT, mục 0) yêu cầu **BlueMail tự viết
toàn bộ máy chủ thư** (SMTP vào/ra, MIME, DNS, DKIM/ARC, SPF/DMARC, lọc rác, kho thư, IMAP). Stalwart,
Postfix, Dovecot, ClamAV… chỉ được dùng **làm đối chứng trong kiểm thử**.

Những gì đã tìm hiểu về Stalwart v1.1.1 trước khi đặc tả đổi (bỏ, không dùng): cấu hình qua JMAP `x:*`,
thư mục SQL chỉ một cột bí mật, mạo danh qua `accountId` với quyền `impersonate`. Không còn liên quan.

## Z1. Mạng kiểm thử dev (`deploy/docker-compose.dev.yml`)

**Quyết định.** Mạng Docker cô lập `10.77.0.0/24` (dải 172.2x đã bị dự án khác trên máy dev chiếm).
Kế hoạch IP cố định (đồng bộ với `scripts/dev-gen-zones.py`):

| IP | Dịch vụ | Ghi chú |
|---|---|---|
| 10.77.0.53 | CoreDNS 1.12.4 | vùng `bluemail.test`, `acme.test`, `doitac.test`, PTR `0.77.10.in-addr.arpa` |
| 10.77.0.10 | PostgreSQL 16.16 | extension `unaccent`, `pg_trgm`, `pgcrypto` |
| 10.77.0.11 | Garage v2.5.0 (S3) | bucket `bm-messages`, `bm-attachments-large`, `bm-exports`, `bm-backups` |
| 10.77.0.12 | Valkey 8.1.12 | |
| 10.77.0.20 | Caddy (web/API/mta-sts/autoconfig) | từ Phase 1 |
| 10.77.0.25 | BlueMail.Mx (`mx.bluemail.test`) | Phase 0: bộ hứng thư Python (`sink`, profile `phase0`) thay chỗ |
| 10.77.0.26 | BlueMail.Outbound (`out1.bluemail.test`) | IP gửi đi, có PTR khớp, nằm trong SPF |
| 10.77.0.27 | BlueMail.Submission (`smtp.bluemail.test`) | |
| 10.77.0.28 | BlueMail.Imap (`imap.bluemail.test`) | Phase 11 |
| 10.77.0.30 | Postfix 3.7 + OpenDKIM (`mail.doitac.test`) | chỉ kiểm thử |
| 10.77.0.40 | Hộp công cụ đối chứng | chỉ kiểm thử |
| 10.77.0.41 | ClamAV 1.5.5 | profile `scanner-compare`, chỉ kiểm thử |
| 10.77.0.42 | Dovecot `imaptest` | profile `imap-compliance`, ghim theo digest (image chỉ có thẻ `latest`) |

Vùng DNS có: MX, A, PTR, SPF (`-all`), DKIM (khoá dev 2048-bit trong `deploy/dev/dkim/`), DMARC
(`bluemail.test`: `p=quarantine`; `doitac.test`: `p=reject; adkim=s; aspf=s`), MTA-STS, TLS-RPT, SRV
RFC 6186, cùng các bản ghi phục vụ kiểm thử DNS: chuỗi CNAME 2 bước (`alias1.bluemail.test`), RRset TXT lớn
(`bigtxt.bluemail.test`, buộc cắt UDP → TCP), MX rỗng RFC 7505 (`nullmx.doitac.test`), tên miền không MX
(`nomx.doitac.test`, kiểm A dự phòng).

Postfix `doitac.test` có hai địa chỉ cố định cho kiểm thử: `tempfail@doitac.test` → `451 4.3.0`,
`reject@doitac.test` → `550 5.1.1`.

**Bằng chứng (2026-11-23).** `scripts/dev-phase0-check.sh` chạy xanh:
- Postfix ký DKIM (`s=dt2026`, relaxed/relaxed, oversign `From`), tra MX `bluemail.test` qua CoreDNS, giao tới
  10.77.0.25; `dkimpy` kiểm chữ ký → `True`.
- Thư gửi vào `binh@doitac.test` nằm trong Maildir; OpenDKIM ghi `Authentication-Results`.
- `451 4.3.0` trả đúng ở `RCPT` cho `tempfail@doitac.test`; TLS 1.3 khi `STARTTLS`.

**Hệ quả.** Mọi phép thử interop chạy được không cần Internet.

## Z2. Công cụ đối chứng

Image `bluemail-dev/tools` (Python 3.12.12-slim-bookworm), **không** nằm trong image chạy thật:
`swaks`, `dig`, `dkimpy 1.1.8`, `authheaders 0.16.3`, `authres 1.2.0`, `checkdmarc 6.0.4`, `dnspython 2.9.2`,
`aiosmtpd 1.4.6`, `vobject 0.9.9` (đối chứng vCard), thư viện chuẩn `email`, `mailbox`, `imaplib`.
ClamAV 1.5.5 và `imaptest` chạy như container riêng theo profile. Lệnh dùng: `docs/06-kich-ban-kiem-thu.md`.

## Z3. Kho S3: **Garage v2.5.0**

**Bằng chứng.** GitHub API (2026-11-23): `minio/minio` — `archived: true`, lần đẩy cuối 2026-04-24, giấy phép
AGPL-3.0; image cộng đồng không còn phát hành. Garage (`deuxfleurs-org/garage`) phát hành đều (v2.5.0), image
`dxflrs/garage:v2.5.0`.

**Quyết định.** Dùng Garage, chạy **nguyên bản, không sửa mã, là dịch vụ riêng** nói chuyện qua giao thức S3 —
nghĩa vụ AGPL-3.0 (công bố mã sửa đổi) không phát sinh với BlueMail. BlueMail chỉ dùng API S3 chuẩn
(`PutObject`, `GetObject` có `Range`, `HeadObject`, `DeleteObject`, `ListObjectsV2`, multipart upload), nên đổi
sang SeaweedFS hoặc S3 đám mây chỉ là đổi cấu hình. Khởi tạo: `scripts/dev-s3-init.sh` (idempotent).

Lưu ý Windows/Git Bash: chạy với `MSYS_NO_PATHCONV=1` để đường dẫn `/garage` không bị đổi.

## Z4. DNSBL / URIBL cho sản phẩm thương mại — **[CẦN CHỦ SẢN PHẨM]**

Bộ chấm điểm rác **phải chạy được khi không có DNSBL nào**; mọi nguồn mặc định **tắt**, quản trị viên bật trong
`spam.dnsbl_sources` sau khi đã có thoả thuận. Tóm tắt điều khoản theo hiểu biết hiện có — **phải xác nhận lại
bằng văn bản với từng nhà cung cấp trước khi bán**:

| Nguồn | Loại | Dùng thương mại | Ghi chú |
|---|---|---|---|
| Spamhaus ZEN / DBL (truy vấn công khai) | IP / tên miền | **Không** cho dịch vụ thương mại; truy vấn công khai còn bị chặn khi qua resolver công cộng | Thương mại: Spamhaus **DQS** (trả phí theo hộp thư/lượt truy vấn) |
| Spamhaus DQS | IP / tên miền | Có, trả phí | Khoá truy vấn riêng; ứng viên chính |
| Barracuda BRBL | IP | Miễn phí, cần đăng ký resolver | Kiểm lại điều khoản dùng cho nhà cung cấp dịch vụ |
| SpamCop | IP | Miễn phí, "tự chịu rủi ro" | Hay có dương tính giả; điểm thấp |
| Mailspike | IP | Miễn phí lưu lượng thấp; thương mại cần giấy phép | |
| PSBL (Passive Spam Block List) | IP | Miễn phí | Điểm thấp |
| SURBL | URI | Thương mại cần giấy phép (miễn phí dưới ngưỡng nhỏ) | |
| URIBL | URI | Thương mại cần giấy phép khi vượt ngưỡng | |
| abuse.ch (URLhaus) | URI | Dữ liệu CC0 | Dùng bản tải về, tra trong bộ nhớ, không tra DNS |

Mỗi nguồn có `license_note` trong CSDL; giao diện quản trị hiện ghi chú này cạnh công tắc.

## Z5. Ước lượng tải (ngưỡng cho phép thử tải)

Giả định một máy 8 nhân / 16 GB phục vụ **2.000 hộp thư** doanh nghiệp:

| Đại lượng | Giả định | Kết quả |
|---|---|---|
| Thư nhận / người / ngày | 60 (kể cả rác bị chặn trong phiên) | 120.000 thư/ngày ≈ 1,4 thư/s trung bình |
| Đỉnh nhận | ×10 trung bình | ≈ 14 thư/s → **ngưỡng phép thử 50 thư/s duy trì** (PROMPT 8.2) dư ~3,5 lần |
| Thư gửi / người / ngày | 20, trung bình 3 người nhận, 20 % ra ngoài | 40.000 thư/ngày; ~24.000 lượt giao ra ngoài |
| Cỡ thư trung bình | 75 KB (kể cả tệp) | ~9 GB/ngày nội dung nhận trước khử trùng |
| Dung lượng S3 / người dùng | tăng 1,5–3 GB/năm (sau khử trùng trong tenant) | 2.000 người ≈ 3–6 TB/năm |
| Kết nối web đồng thời | 25 % người dùng | 500 (PROMPT 8.2) |
| Kết nối IMAP IDLE | 1 người ≈ 1–2 thiết bị | 2.000–4.000 → ngưỡng phép thử 2.000 |

Các số này là giả định ban đầu; Phase 13 đo thật và cập nhật.

## Z6. Cổng 25 ra ngoài và PTR — **[CẦN CHỦ SẢN PHẨM, báo ngay]**

Không kiểm được từ máy dev (mạng gia đình/văn phòng thường chặn cổng 25 ra ngoài) và **không tự kiểm trên hạ tầng
thật khi chưa được phép**. Hiện trạng chung đã biết:

- AWS EC2, Google Cloud, Azure: chặn cổng 25 ra ngoài mặc định; AWS mở theo yêu cầu kèm PTR, GCP **không mở**,
  Azure chỉ mở cho một số loại hợp đồng.
- Nhà cung cấp VPS/IDC Việt Nam (VNPT, Viettel IDC, FPT, CMC…): thường mở theo yêu cầu, PTR đặt qua phiếu hỗ trợ.

Việc chủ sản phẩm cần làm trước Phase 5 (gửi ra ngoài): chọn hạ tầng, xin mở cổng 25 ra, đặt PTR cho từng IP
gửi khớp tên `HELO` (ví dụ `out1.<tên miền dịch vụ>`). Trong lúc chờ, BlueMail.Outbound hỗ trợ **smarthost**
(PROMPT 2.4) để gửi qua dịch vụ chuyển tiếp có sẵn.

## Z7. Danh sách băm mã độc cho `BlueMail.Scanner` (lớp 7)

| Nguồn | Giấy phép | Cập nhật | Cách tải |
|---|---|---|---|
| **MalwareBazaar** (abuse.ch) — danh sách SHA-256 | Dữ liệu **CC0**, cho phép thương mại | liên tục; bản "recent" mỗi 5 phút, bản đầy đủ hằng ngày | HTTPS từ `bazaar.abuse.ch/export/` (cần `Auth-Key` miễn phí của abuse.ch) |
| abuse.ch **URLhaus** | CC0 | liên tục | dùng cho URI xấu trong PDF/HTML (lớp 5–6) |

**Quyết định.** Dùng MalwareBazaar (SHA-256) + URLhaus. Worker tải định kỳ, kiểm định dạng, nạp vào bộ nhớ
(`HashSet` 32 byte/mục; vài triệu mục ≈ <200 MB) và lưu bản sao lên S3 `bm-backups/scanner/`. Không có khoá
`Auth-Key` thì lớp 7 tắt và ghi cảnh báo — bộ quét vẫn chạy lớp 1–6 và 8.
Không dùng chữ ký ClamAV (giấy phép GPL của bộ mẫu không phù hợp nhúng vào sản phẩm).

## Z8. Bộ thư mẫu (`tests/BlueMail.Corpus`)

**Quyết định.** Toàn bộ thư mẫu **tự dựng từng byte** bằng `tests/BlueMail.Corpus/generate.py` (không có dữ liệu
cá nhân, không vướng giấy phép), mô phỏng cấu trúc thư của Outlook, Gmail, Thunderbird, Apple Mail, Yahoo và hệ
thống gửi thông báo tự động. Phiên bản đầu: **46 thư** (27 đúng chuẩn, 19 sai chuẩn):

- Bảng mã: UTF-8, ISO-8859-1/7/8/9, windows-1250/1251/1256/1258 (dấu thanh tổ hợp), KOI8-R, ISO-2022-JP,
  Shift_JIS, GB2312, Big5, EUC-KR, TIS-620; emoji; tiêu đề UTF-8 thô RFC 6532; RTL (Ả Rập, Do Thái).
- Cấu trúc: `multipart/related` + `cid:`, `multipart/alternative`, tên tệp RFC 2231 nhiều đoạn, RFC 2047 B/Q,
  `message/rfc822` lồng, DSN `multipart/report`, `text/calendar` iMIP, `format=flowed`, nhóm địa chỉ, lồng 8 tầng.
- Sai chuẩn: thiếu ranh giới đóng, ranh giới khai khác thực tế, LF trần, 8-bit trong tiêu đề không khai báo
  (UTF-8 và windows-1258), base64 có rác, tiêu đề lặp, nhãn bảng mã sai/lạ, dòng > 998 byte, tiêu đề thiếu dấu
  `:`, không có thân, QP sai, tham số mở ngoặc kép không đóng, lồng 120 tầng (vượt giới hạn), byte NUL, từ mã hoá
  trong chuỗi ngoặc kép, ngày không đọc được, từ mã hoá bị cắt giữa ký tự nhiều byte.

`.gitattributes` đánh dấu `tests/BlueMail.Corpus/**` là `-text` để giữ nguyên CRLF/byte. Mỗi lỗi do phép đối
chứng bắt được sẽ thêm thư gây lỗi vào bộ này (PROMPT 14.23). Thư thật xin phép người dùng sẽ bổ sung khi có,
sau khi bỏ thông tin cá nhân.

Ghi nhận ban đầu từ `email` của Python (để làm danh sách ngoại lệ ở Phase 2): Python không ghép được từ mã hoá
RFC 2047 bị cắt giữa ký tự nhiều byte; ném `LookupError` với bảng mã lạ; hiện `�` với tiêu đề windows-1258
thô. BlueMail chủ ý làm tốt hơn ở các điểm này (PROMPT 2.6.1: không bao giờ ném lỗi làm mất thư).

---

## D1. Image .NET: một image dùng chung (sẽ chốt chi tiết ở Phase 1)

Một image `bluemail/server` chứa mọi tiến trình .NET, chọn tiến trình bằng lệnh khởi động
(`bluemail mx|submission|outbound|imap|api|identity|worker`). Lý do: một lần build, một bộ phụ thuộc, triển khai
và quay lui đồng bộ; các tiến trình vẫn chạy ở container riêng (PROMPT 2).
