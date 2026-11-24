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
sang SeaweedFS hoặc S3 đám mây chỉ là đổi cấu hình. Khởi tạo: `scripts/s3-init.sh` (idempotent).

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

**Đã làm (Phase 6).** Bảng `spam.dnsbl_sources` có sẵn 6 nguồn (Spamhaus ZEN/DBL qua DQS, Barracuda, SpamCop, PSBL,
SURBL), **tất cả tắt**. Tenant bật/tắt qua `PUT /api/v1/admin/spam/lists/{id}` (tạo bản riêng của tenant, không
sửa dòng chung). Mỗi truy vấn tối đa 3 giây; danh sách chậm không bao giờ làm trễ thư. Trả lời 127.255.255.x
(mã lỗi, ví dụ Spamhaus từ chối truy vấn) không tính là bị liệt kê.

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

**Đo ngày 2026-11-24 (mạng dev, VNPT):** cổng 25 ra ngoài mở; IP 14.169.234.77 có PTR chung `static.vnpt.vn` (không FCrDNS) → chưa đủ để gửi tới Gmail/Outlook. Cần IP có PTR đặt được và một tên miền thật.

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

## D2. Băm mật khẩu Argon2id bằng Konscious.Security.Cryptography — **[CẦN CHỦ SẢN PHẨM xác nhận]**

**Bối cảnh.** PROMPT 8.1 yêu cầu Argon2id; nguyên tắc 8 chỉ cho dùng mật mã của `System.Security.Cryptography`,
nhưng BCL .NET 10 **chưa có Argon2**. Hai lựa chọn: (a) PBKDF2-SHA512 của BCL (mặc định ASP.NET Identity),
(b) thư viện Argon2 đã kiểm chứng.

**Quyết định.** Dùng `Konscious.Security.Cryptography.Argon2` 1.3.1 (MIT) — chỉ cho băm mật khẩu và mã khôi phục,
không dùng trong đường xử lý thư. Định dạng chuỗi PHC `$argon2id$v=19$m=19456,t=2,p=1$…` (tham số tối thiểu của
OWASP), đọc lại tham số khi kiểm nên tăng tham số về sau không phá thư cũ (`SuccessRehashNeeded`). Tham số bất thường
từ dòng CSDL bị sửa (m > 1 GiB…) bị từ chối, không đốt CPU. Nếu chủ sản phẩm không chấp nhận thư viện ngoài BCL thì
đổi sang PBKDF2 là thay `IPasswordHasher<User>` — một lớp.

## D3. API kiểm thẻ bằng khoá công khai tĩnh, không gọi discovery

BlueID ký access token (JWT `at+jwt`, RS256, khoá RSA 3072 từ `BM_OIDC_SIGNING_KEY`); API kiểm bằng phần công khai
của cùng khoá (`Microsoft.AspNetCore.Authentication.JwtBearer`), không phụ thuộc mạng tới BlueID lúc kiểm. Access
token sống 15 phút; **mọi yêu cầu** vẫn qua `ISessionValidator` (đệm 30 s, xoá đệm tức thì qua kênh Valkey
`bm:invalidate-user`) nên khoá tài khoản/thu hồi phiên/đổi mật khẩu có tác dụng ngay với thẻ đang cầm. Thẻ mang
`bm_sid` (phiên), `bm_stamp` (security stamp), `bm_tenant`.

## D4. Một tenant "nền tảng"

Quản trị viên nền tảng là người dùng của tenant duy nhất có `kind = PLATFORM` (ràng buộc duy nhất ở CSDL). Lỗi
đăng nhập với địa chỉ không tồn tại được ghi `login_events` dưới tenant này (để mọi dòng vẫn có `tenant_id`).

## D5. Bộ lọc tenant đóng mặc định

Bộ lọc toàn cục EF Core: `tenant_id = tenant hiện tại AND deleted_at IS NULL`. Khi chưa đặt tenant thì **không dòng
nào khớp** (có phép thử). Mã cấp nền tảng phải gọi `IgnoreQueryFilters()` tường minh: tra người dùng lúc đăng nhập
(địa chỉ duy nhất toàn hệ thống), `ISessionValidator`, danh sách tenant của quản trị nền tảng (được canh bằng quyền),
`IMailStore` (luôn ràng `tenant_id` tường minh từ `MailboxAccess`).

## D6. Bảng thư phân vùng

`mail.messages` và `mail.message_labels` phân vùng HASH(tenant_id) 16 phần, tạo bằng migration SQL viết tay
(`MailStorePartitionedTables`, EF loại hai bảng khỏi migration sinh tự động). Khoá chính gồm `tenant_id`; ràng buộc
UID duy nhất là `(tenant_id, label_id, imap_uid)` — tương đương `(label_id, imap_uid)` vì `label_id` là uuid toàn cục.
Cấp UID/modseq bằng `UPDATE … RETURNING` nguyên tử (có phép thử 40 yêu cầu song song).

## D7. Khoá Data Protection lưu trong PostgreSQL

Cookie của BlueID và mã thiết lập 2FA được bảo vệ bằng ASP.NET Core Data Protection, vòng khoá lưu ở
`identity.data_protection_keys` để mọi phiên bản BlueID dùng chung và khởi động lại không mất phiên.

## D8. Thẻ của SPA nằm trong sessionStorage

`oidc-client-ts` lưu thẻ trong `sessionStorage` của gốc web (không cookie đọc được từ nội dung thư); thư HTML luôn
hiển thị trong iframe sandbox không `allow-same-origin` (Phase 4) nên không chạm được kho này. Làm mới bằng refresh
token (`offline_access`), mỗi lần làm mới BlueID hỏi lại `ISessionValidator`.

## D9. Proxy tin cậy

Chỉ đọc `X-Forwarded-For/Proto` từ các dải `BM_TRUSTED_PROXIES` (mặc định 10/8, 172.16/12, 192.168/16 — mạng Docker).
Bằng chứng cần thiết: lần chạy thật đầu tiên mạng Docker là 192.168.16.0/20, mặc định cũ chỉ có 172.16/12 nên BlueID
coi yêu cầu là HTTP và OpenIddict trả `ID2083` (docs/08 BUG-004).

## D10. 7z: không liệt kê được thư mục nén LZMA — **[CẦN CHỦ SẢN PHẨM]**

7-Zip mặc định nén thư mục (kEncodedHeader) bằng LZMA. BCL không có LZMA; nguyên tắc "không thư viện xử lý của bên
thứ ba" (PROMPT 0) không cho thêm thư viện, còn tự viết bộ giải LZMA chỉ để đọc tên tệp là khối lượng lớn. Quyết định
hiện tại: tệp 7z có thư mục nén → `archive_unlistable`, mức theo chính sách (mặc định **nghi ngờ**, quản trị viên có
thể chặn hẳn). 7z với thư mục không nén thì liệt kê tên và áp luật chặn bình thường. Phương án khác cần chủ sản phẩm
quyết: (a) tự viết bộ giải LZMA (ước lượng vài ngày, kèm fuzz), (b) chặn mọi 7z như Gmail không chặn.

## D11. Khác biệt với Python `email` là quyết định có lý do

So sánh với `email` của Python (PROMPT 11) cho thấy 8 điểm BlueMail chủ ý khác (danh sách và lý do trong
`tests/BlueMail.Corpus/python-differences.json`): ở các thư sai chuẩn, BlueMail đọc ra chữ có nghĩa thay vì ký tự
thay thế (đoán bảng mã có kiểm soát, ghép từ mã hoá bị cắt), giới hạn độ sâu lồng, và hiểu `-0000` là UTC theo
RFC 5322. Mục nào trong danh sách không còn khác biệt thì phép thử đỏ để buộc dọn danh sách.

## D12. Khoá của mạng dev nằm trong git (đã xác nhận với chủ sản phẩm)

`deploy/.env.dev` (khoá gốc, khoá OIDC, khoá S3, mật khẩu mẫu) và `deploy/dev/dkim/*.pem` (khoá DKIM của các tên
miền giả `*.test`) được commit có chủ ý để ai clone repo cũng dựng được mạng thử nghiệm ngay. GitGuardian cảnh báo
"Generic Encryption Key / High Entropy Secret" ngày 2026-11-23; đã kiểm: khoá của bản chạy thật (`deploy/.env`,
`deploy/.env.local`) **chưa từng** vào lịch sử git và khác hoàn toàn khoá dev. Chủ sản phẩm quyết định giữ nguyên
(cảnh báo là dương tính giả). Quy tắc: không bao giờ dùng khoá dev cho tên miền thật; `scripts/install.sh` sinh
khoá ngẫu nhiên cho mọi bản cài thật.

## D13. Hàng đợi gửi đi: thuê bao (lease) thay vì giữ khoá giao dịch

Người nhận được "thuê" (`locked_until`) bằng một lệnh UPDATE … `FOR UPDATE SKIP LOCKED` rồi giao dịch kết thúc ngay;
phiên SMTP chạy ngoài giao dịch CSDL. Tiến trình chết → thuê hết hạn → tiến trình khác nhận lại. Kết quả ghi ngay
sau khi máy chủ nhận trả lời. Khoảng hở duy nhất có thể gửi trùng là "máy nhận đã trả 250 nhưng ta chết trước khi
ghi" (mili giây), là bản chất của SMTP (giao ít nhất một lần, RFC 5321 6.1); máy nhận chưa trả lời cuối thì không
tính là đã giao (phép thử `Worker_killed_before_the_final_reply…`).

## D14. Envelope-from là địa chỉ người gửi; VERP từ Phase 12

Thư trả lại đến chậm (bounce không đồng bộ, ARF) được xử lý ở Phase 12 (PROMPT mục 13). Tới lúc đó envelope-from là
chính địa chỉ người gửi để thư trả lại của máy khác về đúng hộp thư người gửi, thay vì một địa chỉ VERP mà `mx` chưa
biết xử lý (sẽ bị từ chối 550 và mất).

## D15. Một cây biểu thức cho tìm kiếm và bộ lọc; gấp chữ bằng PostgreSQL

`SearchParser` (BlueMail.MailStore/Search) dựng cây; `SearchSql` dịch sang SQL tham số hoá cho tìm kiếm, `SearchMatcher`
chạy cây trong bộ nhớ cho bộ lọc lúc giao thư (thư chưa nằm trong CSDL). Để hai lối không lệch nhau, việc bỏ dấu và
chữ thường ở bộ lọc **cũng do PostgreSQL làm** (`PgTextFolder`: một truy vấn `f_unaccent(lower(x))` cho cả lô chuỗi),
đúng hàm mà SQL tìm kiếm dùng. Phép thử `Search_in_sql_and_the_filter_matcher_agree_on_every_operator` chạy 33 câu
trên thư tiếng Việt, Nhật, Trung, Ba Lan, Đức và đòi hai lối cho cùng kết quả.

## D16. Chuyển tiếp: SRS với khoá suy ra từ khoá chủ; trả lời tự động 4 ngày/lần

- SRS0 (hash 4 ký tự base32 từ HMAC-SHA256, ngày mod 1024, hạn 21 ngày); khoá = HKDF(khoá chủ, "bluemail-srs-v1"),
  không thêm biến môi trường. Địa chỉ SRS chỉ nhận thư trả về (người gửi rỗng) — không thành relay mở.
- Thư chuyển tiếp mang bộ ARC của tên miền người dùng (ghi kết quả SPF/DKIM/DMARC đã kiểm) và tiêu đề
  `X-BlueMail-Loop` (mã băm hộp thư); thư đã đi qua hộp thư này hoặc có > 25 `Received` thì không chuyển tiếp nữa.
- Thư chuyển tiếp bị trả lại: DSN của BlueMail.Outbound về **người gửi gốc** (giải SRS), như MTA khác sẽ làm.
- Trả lời tự động: mỗi người gửi một lần trong 4 ngày (như Gmail), bộ đếm xoá khi người dùng lưu lại thiết lập.
  "Chỉ trả lời danh bạ" dùng danh bạ (kể cả "Liên hệ khác" tự lưu khi gửi) từ Phase 8.


## D17. Phân loại và "quan trọng" chỉ bằng quy tắc nhìn thấy được

Không dùng mô hình học máy cho tab và mức quan trọng (PROMPT 6.5/6.6 đòi "giải thích được"). Thứ tự: người trong danh bạ →
Chính; quy tắc tên miền / List-Id của tenant rồi của hệ thống (`mail.category_rules`, quản trị viên sửa được); `List-Id` không
kèm hủy đăng ký → Diễn đàn; `List-Unsubscribe` hoặc `Precedence: bulk` → Quảng cáo; `Auto-Submitted` hoặc người gửi
noreply/notifications → Cập nhật; còn lại → Chính. Quan trọng: người mình gửi thư ≥ 3 lần, hoặc đồng nghiệp cùng tổ chức gửi
thẳng (To) cho mình; thư hàng loạt không bao giờ tự thành quan trọng. Bộ lọc của người dùng luôn được áp sau và thắng.

## D18. Cài đặt người dùng ở máy chủ, một tài liệu JSON

`mail.user_settings.settings` (jsonb) theo PROMPT mục 4; máy chủ chuẩn hoá mọi giá trị (giá trị lạ về mặc định) nên một máy khách
cũ không thể lưu thứ máy chủ không đọc được. Giao diện (chủ đề, mật độ, khung đọc, hoàn tác) đọc từ đây và theo người dùng trên
mọi thiết bị; store cục bộ chỉ là bản sao.

## D19. Báo cáo: XLSX tự viết, PDF qua hộp thoại in của trình duyệt

PROMPT 7.5 đòi xuất Excel và PDF. XLSX là gói ZIP + vài tệp XML (SpreadsheetML), tự viết được bằng `System.IO.Compression`
(`Xlsx.cs`, có kiểm đơn vị); không cần thư viện. PDF: một bộ dựng PDF đúng chuẩn có phông tiếng Việt nhúng là việc lớn và
không đem lại gì hơn hộp thoại in của trình duyệt — trang báo cáo có kiểu in riêng (`@media print`: ẩn thanh điều hướng,
bộ lọc, phân trang) nên "In / PDF" → "Lưu dưới dạng PDF" cho đúng bảng và biểu đồ đang xem. Mọi tệp xuất ghi rõ khoảng thời
gian và "đầy đủ / bị cắt ở N dòng" (PROMPT 14).

## D20. Chính sách: khoá có kiểu, kế thừa đơn vị → tổ chức → mặc định

`admin.policies (tenant_id, org_unit_id NULL, key, value jsonb)`, duy nhất theo (tenant, đơn vị, khoá). Danh sách khoá, kiểu và
mặc định nằm ở một chỗ (`PolicyKeys`); API từ chối khoá lạ (404) và giá trị sai kiểu (400). Mỗi khoá phải có nơi đọc — phép thử
quy ước `Every_policy_key_has_a_reader` đỏ khi có khoá không ai dùng, để không có "cài đặt chỉ để trưng". Bảng
`tenant.mail_policy` của Phase 7 được chuyển dữ liệu vào đây rồi bỏ.

## D21. Đăng nhập hộ để hỗ trợ: phiếu một lần, phiên có người thực hiện

Quản trị nền tảng nhập lý do → phiếu ngẫu nhiên 256 bit (lưu băm SHA-256, dùng một lần, 10 phút) → BlueID tạo phiên 1 giờ cho
người dùng với `actor_user_id` của quản trị viên. Nhật ký kiểm toán ghi cả lúc tạo phiếu và lúc dùng; quản trị viên của tổ chức
nhận thư báo; giao diện người dùng hiện dải cảnh báo suốt phiên. Không có cách đăng nhập hộ im lặng.

## D22. Giữ pháp lý và lưu giữ: đếm lại từ nguồn, quyết định trong chính câu xoá

`mail.messages.legal_hold_count` là cột chép sẵn, luôn tính lại bằng `compliance.f_recompute_hold_counts` (không cộng dồn,
PROMPT 14.4) khi đặt/gỡ lệnh giữ và khi người dùng đổi phòng ban; thư mới nhận số của hộp thư ngay trong câu INSERT. Lệnh giữ
theo người/phòng ban phủ cả thư đến sau; lệnh giữ theo truy vấn phủ những thư khớp lúc đặt (`compliance.hold_messages`, tối đa
500 000). "Xoá vĩnh viễn" (người dùng, dọn Thùng rác 30 ngày, quy tắc DELETE_AFTER) dùng một câu UPDATE duy nhất: luôn đặt
`deleted_at` (biến khỏi giao diện), chỉ đặt `expunged_at` khi `legal_hold_count = 0` và đã qua KEEP_MIN — cùng một câu, nên không
có khe giữa kiểm tra và xoá. Thư bị ẩn mà sau này hết bị giữ được `ExpungeReleasedAsync` xoá hẳn mỗi giờ. KEEP_MIN không có điều
kiện tìm (phải kiểm được trong câu xoá); DELETE_AFTER có.

## D23. Nhập thư: máy khách IMAP tự viết, chống trùng hai lớp, không mở SSRF

Nguồn IMAP: chỉ địa chỉ công khai và cổng 143/993 (cấu hình được cho mạng thử nghiệm), mật khẩu mã hoá bằng khoá tenant và xoá
khi việc nhập kết thúc. Gmail: chỉ đọc `[Gmail]/All Mail` (nhãn qua `X-GM-LABELS`) cùng Thư rác/Thùng rác. Chống trùng: vị trí
nguồn (tài khoản + thư mục + UIDVALIDITY + UID, hoặc băm tệp + vị trí) là khoá duy nhất; cùng nội dung (SHA-256) gặp ở thư mục
khác chỉ thêm nhãn. Một việc nhập mỗi hộp thư (ràng buộc duy nhất). Thư nhập không chạy bộ lọc/trả lời tự động/chuyển tiếp
nhưng vẫn qua bộ quét tệp. mbox theo mboxrd khi ghi; đọc chấp nhận cả mboxo (dòng ">From " gốc trong tệp mboxo mất một '>' —
giới hạn vốn có của định dạng). Nghiệm thu với Gmail/Outlook.com thật cần hộp thư thử nghiệm của chủ sản phẩm (docs/06 P10-12).

## D24. IMAP: nhãn là thư mục, cờ dùng chung, \Recent trong tiến trình, tìm kiếm chính xác có giới hạn

Như Gmail IMAP: mỗi nhãn là một thư mục, một thư ở nhiều thư mục; `\Seen`, `\Flagged`, `\Answered`, `$Forwarded`, từ khoá
thuộc về thư (mọi thư mục thấy giống nhau), còn `\Deleted` thuộc về cặp (thư, thư mục) (`mail.message_labels.imap_deleted`).
EXPUNGE ở Thùng rác/Thư rác là xoá vĩnh viễn (qua câu xoá duy nhất, D22), ở thư mục khác là gỡ nhãn. COPY = thêm nhãn, MOVE =
thêm nhãn + gỡ nhãn nguồn; Starred/Important là cờ. "Tất cả thư" chưa có thư mục IMAP (cần nhãn ảo có UID riêng — để sau).
`\Recent` (RFC 3501) do tiến trình IMAP nhớ (thư mục → UID cao nhất đã có phiên thấy); khởi động lại thì không thư nào
"recent" — RFC 9051 đã bỏ cờ này, phần mềm thư hiện nay không dựa vào nó. SEARCH: thư mục tới 5 000 thư thì đọc thư thật
(chuỗi con trong tiêu đề đã giải mã và các phần văn bản, SENT* theo múi giờ ghi trong Date); lớn hơn dùng cột chỉ mục và chỉ
mục từ (nhanh, theo từ). Kiểm chứng: `imaptest` của Dovecot 0/460 lệnh cơ bản sai.

## D25. Đăng nhập phần mềm thư: mật khẩu, mật khẩu ứng dụng, thẻ BlueID

IMAP và SMTP nộp thư chỉ nhận mật khẩu sau TLS. Mật khẩu tài khoản dùng được khi chưa bật 2FA; khi đã bật thì bắt buộc mật khẩu
ứng dụng (16 chữ, lưu SHA-256 — đủ vì ngẫu nhiên 75 bit, không phải mật khẩu người đặt). XOAUTH2/OAUTHBEARER nhận access token của
BlueID (kiểm chữ ký, issuer, audience, rồi `ISessionValidator` như web). Dò mật khẩu: 10 lần sai/tài khoản, 30 lần/IP trong 15 phút
thì khoá tạm, không kiểm tiếp (không làm "máy hỏi mật khẩu"). Thu hồi mật khẩu ứng dụng, khoá tài khoản, đổi mật khẩu → kênh
Valkey `bm:invalidate-user` → kết nối IMAP đang mở của người đó bị đóng ngay.

## D26. DNSSEC tự kiểm, DANE trước MTA-STS, gốc không có DNSSEC thì "không xác định"

`BlueMail.Dns.DnssecValidator` hỏi máy phân giải với DO+CD (lấy chữ ký, không để máy phân giải lọc) và tự dựng chuỗi tin
cậy từ khoá gốc KSK-2017/KSK-2024 (DS SHA-256 nhúng trong mã) qua DS → DNSKEY; chữ ký thuật toán 8, 13, 14 bằng
`System.Security.Cryptography`; NSEC/NSEC3 chứng minh không tồn tại (kể cả opt-out). Vùng chỉ ký bằng thuật toán khác
= không an toàn (RFC 4035 §5.2). Một bản ghi NSEC có NS mà kèm A/TXT… (kiểu "black lies" của CoreDNS, Cloudflare)
**không** phải điểm uỷ quyền (BUG-061). Nếu máy phân giải không trả DNSKEY của gốc thì kết quả là `Indeterminate`
("không có DNSSEC"), không phải `Bogus`: trên mạng không có DNSSEC, gửi thư vẫn chạy như trước khi có DANE, log ghi rõ.
Sai chữ ký thật → `Bogus` → không gửi tới MX đó (RFC 7672 §2.2.1). DANE dùng DANE-TA/DANE-EE, bỏ qua PKIX-*; có DANE thì
không xét MTA-STS (RFC 8461 §2). Vùng ký riêng (dev `doitac.test`) tin qua `BM_DNSSEC_ANCHORS`. Kiểm chứng: cùng kết quả
với bộ kiểm dựng trên dnspython (`tests/interop/dnssec_check.py`) trên 16 tên thật và vùng dev do CoreDNS ký.

## D27. VERP, thư trả lại muộn, ARF và tự khoá gửi

`MAIL FROM` thư của người dùng = `bounces+<id hàng đợi>.<12 hex HMAC>@<tên miền người gửi>` (khoá dẫn xuất từ khoá chủ,
khác khoá SRS): SPF vẫn căn chỉnh DMARC vì cùng tên miền, MX chỉ nhận VERP có mã kiểm đúng (không thành chỗ chuyển tiếp
mở). Thư hệ thống (DSN, chuyển tiếp SRS) giữ địa chỉ cũ. Báo cáo trả lại muộn / ARF được xử lý trong MX (không lưu như
thư thường); mỗi (thư, người nhận, loại) ghi một lần (ràng buộc duy nhất). Ngưỡng tự khoá (24 giờ, ≥ 20 người nhận:
trả lại cứng > 10 %, hoặc ≥ 3 khiếu nại và > 0,3 %) là mức các nhà cung cấp lớn coi là lạm dụng; khoá ở cột
`identity.users.sending_locked_at`, chỉ quản trị viên tổ chức mở, có nhật ký kiểm toán.

## D28. Dải IP, làm ấm, PTR

Dải IP là hạ tầng nền tảng (không có `tenant_id`, như `mta.destination_limits`); tổ chức trỏ tới dải qua
`tenants.ip_pool_id`. Trần làm ấm theo ngày UTC, đếm số người nhận đã giao (`mta.sending_ip_days`, upsert cộng dồn).
Thư bị giữ vì trần không tính là một lần thử (không bao giờ dẫn tới DSN). IP chỉ được dùng khi PTR = HELO và HELO trỏ lại
IP (kiểm mỗi giờ). Không có dải áp dụng cho tổ chức → địa chỉ của máy (BUG-062).

## D29. Chế độ bảo mật

Chỉ người nhận ngoài hệ thống nhận liên kết; người trong hệ thống nhận thư (đã ở trong cùng kho, cùng chính sách giữ
thư). Mã liên kết = HMAC(khoá, id thư + địa chỉ) nên gửi lại sau sự cố tạo đúng liên kết cũ và không xếp hàng thêm
(chống trùng bằng Message-ID của thư báo); chỉ lưu SHA-256 của mã. Nội dung là bản đã soạn (blob mã hoá của tenant),
hiển thị qua cùng bộ lọc HTML như khi đọc thư, ảnh ngoài luôn chặn. Mã 6 số: 10 phút, 5 lần sai, 1 mã/phút. Không
có DRM: trang nói rõ không ngăn được chụp màn hình.

## D30. Danh sách hội thoại ở quy mô lớn: đọc có trần, gom trong bộ nhớ hoặc duyệt theo thời gian

Trước Phase 13 mỗi lần mở danh sách đếm và gom **mọi** thư của nhãn theo hội thoại (O(N), hai lần): 100k thư ≈ 390 ms,
600k thư 1,8 s; tìm kiếm trên 1M thư p95 ≈ 3 s. Nay (`PgMailStore.ListConversationsAsync`): một truy vấn đọc tối đa
`CountCap + 1` = 10.001 thư khớp theo bất kỳ thứ tự nào chỉ mục cho (GIN, trigram, nhãn). ≤ 10.000: gom trong bộ nhớ,
tổng chính xác. Lớn hơn: tổng là "hơn 10.000" (`TotalExact = false`, web hiện "1–50 trên rất nhiều") và trang được dựng
bằng cách duyệt thư mới nhất trước theo chỉ mục `(tenant, mailbox, received_at, id)`, dừng khi đủ hội thoại. Con trỏ là
thư mới nhất của hội thoại cuối trang (thời gian, id theo thứ tự uuid của PostgreSQL) — cả hai đường dùng chung nghĩa,
phép thử `ConversationPagingTests` buộc chúng cho cùng kết quả. Chỉ mục bao phủ hẹp `ix_messages_list_cover` và trigram
đúng biểu thức tìm `subject:`/`from:` (migration `MessageListCoverIndex`).
Sắp xếp theo người gửi / tiêu đề / kích thước (Outlook "Sort by") chỉ dùng đường gom trong bộ nhớ: cùng một lần đọc lấy thêm
`from_name/from_addr`, `normalized_subject`, `size`, sắp các hội thoại rồi chia trang bằng con trỏ vị trí `o:<n>`. Danh sách vượt
`CountCap` vẫn theo ngày; `ConversationPage.Sort` cho web biết thứ tự đã dùng để báo người dùng.

## D31. Tiến trình `worker`, kích thước pool kết nối

`api` và `worker` cùng một tệp chạy, chọn bằng `BM_ROLE` (`api` chỉ phục vụ HTTP, `worker` chạy việc nền; không đặt =
cả hai, cho dev). Mỗi tiến trình đặt `BM_DB_MAX_POOL` (identity/worker/submission/outbound 20, api/mx/imap 40 — tổng
~200) và PostgreSQL `max_connections=300`: khi 2.000 phiên IMAP IDLE cùng được đánh thức, tiến trình IMAP chờ trong
pool của mình thay vì làm cạn kết nối của MX (BUG-069).

## D32. PWA không lưu thư trên máy

Service worker (`web/public/sw.js`) chỉ cache khung ứng dụng (tệp `/assets/*` có băm nội dung, trang gốc, biểu tượng);
không cache API, tệp đính kèm hay bất cứ dữ liệu người dùng nào — máy dùng chung/mất máy không lộ thư. Ngoại tuyến: mở
được khung và báo không kết nối. Các trang ít dùng (quản trị, cài đặt, danh bạ, trang xem thư bảo mật) và trình soạn
thảo (tiptap), xem PDF được tải khi cần: gói đầu từ 2,9 MB xuống 510 kB (162 kB gzip).

## D33. Số đếm nhãn nhớ theo modseq

Mọi thay đổi có thể làm đổi số đếm (giao thư, đổi cờ/nhãn, xoá, tạo/xoá nhãn) đều cấp `highest_modseq` mới của hộp thư
trong cùng giao dịch. `MailboxCountCache` (mỗi tiến trình) nhớ số đếm theo nhãn và dung lượng theo (hộp thư, modseq):
cùng modseq thì đúng bằng kết quả đếm lại, nên vẫn giữ "số đếm lấy từ chính các hàng" (PROMPT 14.7); các yêu cầu đồng
thời cùng modseq dùng chung một lần tính. Mỗi lượt xem chỉ còn một truy vấn đọc `highest_modseq`.


## D34. Dọn blob S3 không còn tham chiếu

Blob đánh địa chỉ theo nội dung (một object cho mỗi tenant + SHA-256), nhiều nơi cùng dùng: thư, hàng đợi gửi đi,
cách ly, thư bảo mật, ảnh danh bạ. Trước đây không bao giờ xoá, nên thư bị xoá hẳn vẫn chiếm dung lượng S3.

- Mọi nơi ghi gọi `BlobRefs.EnsureAsync`: cập nhật `mail.blobs.last_used_at` (UPDATE trước, nên chờ nếu bộ dọn đang
  giữ khoá dòng), kiểm tra object còn trên S3, thiếu thì PUT lại, rồi mới ghi tham chiếu của mình.
- `BlobRefs.CollectAsync` (chạy mỗi giờ trong `MailMaintenanceWorker`, tối đa 1.000 blob/lượt): chọn blob không có
  tham chiếu nào (thư chưa expunge, `mta.outbound_messages`, `admin.quarantine`, `mail.confidential_messages`, ảnh
  `contact.contacts`) và không dùng trong 2 ngày, `FOR UPDATE SKIP LOCKED`, **xoá object S3 khi còn giữ khoá dòng**,
  đánh dấu `collected_at` rồi mới commit. Dòng `mail.blobs` được giữ vì thư đã expunge vẫn trỏ tới (khoá ngoại).
- An toàn khi chạy đồng thời: người ghi đã đánh dấu trước thì được bảo vệ bởi thời gian chờ 2 ngày; người ghi đến
  sau phải chờ khoá, rồi thấy object đã mất và PUT lại. Nếu bộ dọn xoá object nhưng commit thất bại, lần ghi sau cũng
  thấy object thiếu và PUT lại (`BlobCollectionTests`).
- Object ghi trước bản này mà không có dòng `mail.blobs` (thư hệ thống gửi ra ngoài, ảnh danh bạ) không bao giờ bị
  dọn: thà sót còn hơn xoá nhầm.

## D35. Hangfire cho việc định kỳ; hàng đợi cần độ trễ thấp giữ worker thuê bao

Spec (mục 1, 8.3, 9) chọn Hangfire cho việc nền nghiệp vụ, lưu trong PostgreSQL (schema `hangfire`), Dashboard chỉ cho quản
trị nền tảng. Các phase đầu đã viết các hàng đợi bằng worker thuê bao (`FOR UPDATE SKIP LOCKED` + lease, như D13), có tiến độ,
chống chạy trùng và tự nhận lại việc chết, và đã được kiểm kỹ.

- Hangfire (`Jobs/PeriodicJobs`) chạy việc **định kỳ**: dọn Thùng rác/Thư rác 30 ngày (mỗi giờ), dọn blob S3 (D34, mỗi giờ),
  kiểm DNS tên miền (mỗi 30 phút, mỗi tên miền 6 giờ một lần). `DisableConcurrentExecution` (khoá phân tán) nên nhiều
  tiến trình worker không chạy trùng; việc an toàn khi chạy lại. Server Hangfire chỉ chạy ở vai trò `worker`/`all`;
  vai trò `api` chỉ mở storage cho Dashboard.
- Giữ worker thuê bao cho hàng đợi cần **độ trễ vài giây** và có trạng thái riêng mà giao diện đọc: thao tác hàng loạt,
  áp bộ lọc cho thư cũ, nhập/xuất và eDiscovery, trả thư tạm ẩn; và hàng đợi gửi thư (spec đã loại khỏi Hangfire).
  Lịch tối thiểu của Hangfire là 1 phút và trạng thái của nó không thay được bảng tiến độ người dùng xem.
- Dashboard `/hangfire`: trình duyệt không gửi bearer token, nên quản trị nền tảng gọi `POST /api/v1/admin/platform/jobs-dashboard`
  để nhận cookie `bm_jobs` (ký bằng Data Protection chung khoá với BlueID, HttpOnly, SameSite=Strict, Path=/hangfire, 1 giờ,
  gắn phiên). Mỗi request Dashboard kiểm cookie và kiểm lại phiên qua `ISessionValidator` (đăng xuất, bị khoá, mất quyền,
  phiên hỗ trợ → không vào được). Bộ lọc "chỉ request nội bộ" mặc định của Hangfire bị tắt vì sau proxy nó cho mọi người vào.
  CSP riêng cho `/hangfire`: script chỉ từ chính nó, không inline.
- Newtonsoft.Json (phụ thuộc của Hangfire) được ghim bản vá 13.0.4.
