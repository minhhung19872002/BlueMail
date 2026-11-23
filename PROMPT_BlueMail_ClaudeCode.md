# **BlueMail** — DỊCH VỤ THƯ ĐIỆN TỬ DOANH NGHIỆP KIỂU GMAIL, LÕI TỰ PHÁT TRIỂN

> Đặc tả để Claude Code xây dựng BlueMail từ đầu, **kể cả máy chủ thư**. Đọc hết trước khi viết dòng mã đầu
> tiên. Làm tuần tự theo mục 13, không nhảy phase.

---

## 0. VAI TRÒ, MỤC TIÊU, NGUYÊN TẮC

Bạn là kỹ sư chính xây dựng **BlueMail** — dịch vụ thư điện tử theo tên miền riêng cho doanh nghiệp
(`ten@congty.vn`), bán dạng dịch vụ đám mây nhiều khách hàng **và** dạng bản cài riêng tại máy chủ của
khách. Trải nghiệm người dùng ngang Gmail; quản trị ngang Google Admin Console phần Gmail. BlueMail là sản
phẩm đứng riêng, chạy được một mình.

**Khác biệt cốt lõi: BlueMail tự viết toàn bộ máy chủ thư** — nhận thư (SMTP vào), gửi thư (hàng đợi và SMTP
ra), xác thực thư (SPF, DKIM, DMARC, ARC), lọc thư rác, lưu hộp thư, và về sau là IMAP cho Outlook/điện thoại.
Không dùng máy chủ thư có sẵn nào (Stalwart, Postfix, Dovecot, Exim, Haraka, Rspamd…) **làm thành phần chạy
thật**. Được phép dùng chúng **làm đối chứng trong kiểm thử** (mục 11).

**Ranh giới "tự phát triển":** BlueMail **không dùng thư viện xử lý thư, DNS hay quét virus của bên thứ ba**.
Chỉ được dựa vào **nền tảng .NET** (runtime và thư viện chuẩn: `System.Net.Sockets`, `SslStream`,
`System.Security.Cryptography`, `System.IO.Compression`, `System.Text.Encoding.CodePages`, Kestrel) và các
thành phần hạ tầng đã liệt kê ở mục 1 (PostgreSQL, S3, Valkey, Hangfire, OpenIddict, EF Core, MediatR…).

| Thành phần | Tự viết thành dự án riêng | Chuẩn phải tuân theo |
|---|---|---|
| Phân tích và dựng thư | `BlueMail.Mime` | RFC 5322, 2045–2049, 2047, 2231, 2183, 2392, 3676, 6532 |
| Tra DNS | `BlueMail.Dns` | RFC 1035, 3596, 6891 (EDNS0), 7766 (TCP); DNSSEC (RFC 4033–4035) ở Phase 12 |
| Ký và kiểm DKIM, ARC | `BlueMail.Dkim` | RFC 6376, 8301, 8617 |
| Quét tệp đính kèm | `BlueMail.Scanner` | Mục 2.6.4 |
| Máy khách IMAP (chỉ cho công cụ chuyển thư sang) | `BlueMail.Protocols.Imap` (dùng chung bộ phân tích với máy chủ IMAP) | RFC 3501, 9051 |
| Máy chủ SMTP/IMAP, hàng đợi gửi, SPF, DMARC, lọc rác, kho thư | như mục 2 | như mục 2 |

Không dùng máy chủ thư có sẵn nào (Stalwart, Postfix, Dovecot, Exim, Haraka, Rspamd…) và không dùng ClamAV
**làm thành phần chạy thật**. Phần mềm của người khác **chỉ được xuất hiện trong kiểm thử, làm đối chứng**
(mục 11) — đó là cách duy nhất chứng minh mã tự viết đúng chuẩn.

Thêm bất kỳ thư viện nào khác vào đường xử lý thư thì phải ghi lý do vào `docs/00-quyet-dinh-ky-thuat.md` và
hỏi chủ sản phẩm.

**Nguyên tắc không thương lượng:**

1. **Một kho thư, một cửa ghi.** Mọi thay đổi hộp thư (thư mới đến, gắn nhãn, đọc, xoá, di chuyển) đi qua
   **một** dịch vụ miền `IMailStore` dùng chung cho API web, máy chủ SMTP và máy chủ IMAP. Không chỗ nào ghi
   thẳng bảng thư ngoài `IMailStore`. Mỗi lần ghi tăng `modseq` và phát sự kiện — đó là thứ làm SignalR và
   IMAP IDLE chạy đúng.
2. **Không stub, không mock trong mã bàn giao, không TODO.** Chức năng nào chưa làm được thì dừng lại hỏi.
3. **Tuân thủ chuẩn RFC là yêu cầu chức năng**, không phải "làm thêm". Mỗi thành phần giao thức ghi rõ RFC
   nó tuân theo và có phép thử với **phần mềm của người khác** (mục 11).
4. **Sản phẩm quốc tế, đa ngôn ngữ ngay từ đầu** (mục 0.2). Không chuỗi giao diện hay thông báo lỗi nào viết
   cứng trong mã; ngôn ngữ gốc là **tiếng Anh**, kèm sẵn **tiếng Việt**. Mã nguồn, tên biến, chú thích, commit
   bằng tiếng Anh. Mã phản hồi SMTP/IMAP giữ đúng tiếng Anh theo chuẩn (phần mềm khác đọc chúng).
5. **`tenant_id` có mặt trong mọi bảng nghiệp vụ ngay từ đầu**, kể cả khi bản cài riêng chỉ có một khách.
6. **Bảo mật mặc định đóng**: không làm relay mở, từ chối người nhận không tồn tại ngay ở lệnh `RCPT`, ảnh từ
   xa bị chặn tới khi người dùng cho phép, HTML thư đến luôn được lọc và hiển thị trong iframe cách ly, tệp
   đính kèm luôn quét virus.
7. **Bộ phân tích dữ liệu từ Internet là mặt trận bị tấn công đầu tiên.** Mọi đầu vào từ mạng (lệnh SMTP,
   lệnh IMAP, MIME, bản ghi DNS, tệp đính kèm) đều có giới hạn độ dài, giới hạn độ sâu, giới hạn thời gian, và
   được thử bằng fuzzing.
8. **Tự viết giao thức, không tự viết mật mã.** RSA, SHA-256, AES-GCM, sinh số ngẫu nhiên, TLS: **chỉ** dùng
   `System.Security.Cryptography` và `SslStream` của .NET. Thuật toán nào .NET chưa có (ví dụ Ed25519) thì
   **không làm**, không tự cài đặt.

### 0.1. Định danh sản phẩm

| Hạng mục | Giá trị |
|---|---|
| Tên sản phẩm | **BlueMail** |
| Tên đầy đủ | BlueMail Business Email (tiếng Việt: Dịch vụ thư điện tử doanh nghiệp BlueMail) |
| Solution .NET | `BlueMail.slnx`, namespace gốc `BlueMail.*` |
| Dịch vụ đăng nhập | **BlueID** (dự án `BlueMail.Identity` trong cùng solution, OIDC chuẩn) |
| Tên máy chủ trong lời chào SMTP/IMAP | Lấy từ cấu hình (`BM_MAIL_HOSTNAME`), không viết cứng |
| Docker compose project | `bluemail` |
| CSDL PostgreSQL | `bluemail` (schema: `identity`, `tenant`, `mail`, `mta`, `spam`, `contact`, `admin`, `compliance`, `audit`, `hangfire`) |
| Tiền tố biến môi trường | `BM_` |
| Bucket lưu trữ | `bm-messages` (nội dung thư gốc), `bm-attachments-large`, `bm-exports`, `bm-backups` |
| Tên miền dev | `bluemail.test`, `acme.test` (hai khách của BlueMail), `doitac.test` (máy chủ "bên ngoài" — Postfix, chỉ dùng để kiểm thử) |

Tên khách hàng, logo, tên miền **là dữ liệu cấu hình**, không bao giờ viết cứng vào mã.

### 0.2. Quốc tế hoá — yêu cầu xuyên suốt

BlueMail bán cho khách ở mọi nước. **Không có ngôn ngữ, quốc gia, múi giờ hay bảng chữ cái nào được ưu tiên
trong mã.** Mọi chỗ trong tài liệu này nhắc tới một ngôn ngữ cụ thể chỉ là ví dụ kiểm thử.

- **Ngôn ngữ giao diện**: tiếng Anh (`en`, gốc và dự phòng) + tiếng Việt (`vi`) có sẵn; thêm ngôn ngữ mới chỉ
  là thêm một tệp dịch, không sửa mã. Ngôn ngữ theo cài đặt người dùng → mặc định của tenant → `Accept-Language`.
  Dùng dạng số nhiều và chèn biến theo ICU (không ghép chuỗi). Phép thử: mọi khoá có đủ ở mọi ngôn ngữ; không
  chuỗi hiển thị nào nằm ngoài tệp dịch (quét cả TSX lẫn C#).
- **Thông báo từ máy chủ** (lỗi API, thư hệ thống, DSN, thư xác minh, trả lời vắng mặt mặc định): theo ngôn ngữ
  của người nhận thông báo; lỗi API trả kèm **mã lỗi ổn định** (`error.code`) để máy khách tự dịch. DSN gửi song
  ngữ: tiếng Anh + ngôn ngữ của người gửi.
- **Chữ viết phải-sang-trái** (Ả Rập, Do Thái): giao diện lật theo `dir="rtl"` (dùng thuộc tính logic CSS:
  `margin-inline-start`…, không `left`/`right`); thân thư và ô soạn tự nhận hướng chữ (`dir="auto"`).
- **Ngày giờ, số, dung lượng**: định dạng theo ngôn ngữ và múi giờ của người dùng (`Intl` ở trình duyệt,
  `CultureInfo` theo từng yêu cầu ở máy chủ); người dùng chọn 12/24 giờ và ngày đầu tuần. Múi giờ mặc định lấy
  từ tenant, lưu theo tên IANA. **Tiến trình máy chủ chạy `InvariantCulture`**; không đặt văn hoá mặc định của
  một nước nào.
- **Tên người**: một trường `display_name` là chính; không giả định thứ tự họ–tên, không bắt buộc tách họ/tên.
- **Văn bản**: lưu và so sánh Unicode chuẩn hoá NFC; mọi phép cắt chuỗi cắt theo cụm chữ (grapheme), không cắt
  giữa ký tự ghép hay emoji.
- **Tìm kiếm cho mọi ngôn ngữ** (mục 4): bỏ dấu cho mọi chữ Latin có dấu (Việt, Pháp, Đức, Tây Ban Nha, Séc…),
  gập hoa/thường theo Unicode; chữ **không có khoảng trắng giữa các từ** (Trung, Nhật, Thái, Hàn…) lập chỉ mục
  theo cặp ký tự (bigram) để tìm được chuỗi con. Không dùng bộ tách gốc từ của riêng một ngôn ngữ.
- **Thư quốc tế hoá**: địa chỉ có ký tự ngoài ASCII (`SMTPUTF8`, RFC 6531/6532), tên miền quốc tế hoá
  (IDNA2008/UTS #46 — lưu dạng A-label `xn--`, hiển thị dạng Unicode, **cảnh báo khi tên miền trộn nhiều hệ chữ**),
  mọi bảng mã thư cũ (`System.Text.Encoding.CodePages`: Latin, Kirin, Hy Lạp, Thổ, Do Thái, Ả Rập, Thái, Việt,
  Trung giản/phồn thể, Nhật, Hàn).
- **Ghép hội thoại và soạn thư**: bảng tiền tố trả lời/chuyển tiếp của nhiều ngôn ngữ (`Re`, `Fwd`, `FW`, `AW`,
  `WG`, `RV`, `TR`, `SV`, `VS`, `Odp`, `回复`, `答复`, `转发`, `返信`, `Trả lời`, `TL`…), là dữ liệu cấu hình.
  Bộ từ khoá "nhắc tới tệp đính kèm mà không đính kèm" cũng theo từng ngôn ngữ.
- **Lọc rác**: tách từ không phụ thuộc ngôn ngữ (từ theo ranh giới Unicode UAX #29 + bigram cho chữ không
  khoảng trắng); không luật nào phạt thư chỉ vì ngôn ngữ hay bảng chữ.
- **Tuân thủ pháp lý nhiều vùng**: chọn **vùng lưu dữ liệu** theo tenant (`data_region`); đủ công cụ cho GDPR và
  luật tương tự — xuất toàn bộ dữ liệu của một người, xoá vĩnh viễn theo yêu cầu (trừ khi đang giữ pháp lý), nhật
  ký truy cập, thời hạn lưu cấu hình được; trang đăng nhập có liên kết điều khoản và chính sách riêng tư do tenant
  cấu hình.
- **Tài liệu**: tài liệu người dùng và quản trị viết tiếng Anh và tiếng Việt; tài liệu kỹ thuật nội bộ
  (`docs/00`, `docs/08`) một ngôn ngữ là đủ.
- **Truy cập**: WCAG 2.1 AA.
- **Dữ liệu mẫu và bộ thư kiểm thử** phải có nhiều ngôn ngữ và hệ chữ (Latin có dấu, Kirin, CJK, Ả Rập, emoji).

---

## 1. STACK BẮT BUỘC

| Thành phần | Công nghệ | Ghi chú giấy phép / phiên bản |
|---|---|---|
| Backend | **.NET 10**, ASP.NET Core | Clean Architecture + CQRS (MediatR) cho API |
| Máy chủ giao thức | Kestrel `ConnectionHandler` + `System.IO.Pipelines` | SMTP và IMAP chạy như dịch vụ TCP trong .NET |
| ORM | EF Core 10 + Npgsql, `EFCore.NamingConventions` (snake_case) | Đường nóng của SMTP/IMAP được phép dùng Npgsql trực tiếp, gom ở `Infrastructure/MailStore` |
| CSDL | **PostgreSQL 16** | `unaccent`, `pg_trgm` cho tìm kiếm đa ngôn ngữ (mục 0.2) |
| Lưu trữ đối tượng | Dịch vụ tương thích S3 | Phase 0 kiểm giấy phép và tình trạng phát hành image của MinIO; nếu không ổn dùng **Garage** hoặc **SeaweedFS** |
| Bộ đệm / backplane SignalR | **Valkey** (BSD) | Không dùng Redis ≥ 7.4 (đổi giấy phép) |
| Việc nền | **Hangfire** (PostgreSQL storage, schema `hangfire`) | Việc nghiệp vụ (hẹn giờ, tạm ẩn, xuất dữ liệu). **Hàng đợi gửi thư không dùng Hangfire** — xem 2.4 |
| Thời gian thực | **SignalR** | |
| Đăng nhập | **OpenIddict** (Apache-2.0) + ASP.NET Core Identity | |
| MIME, DKIM, ARC, DNS, máy khách IMAP | **Tự viết** (mục 2.6) | Không dùng MimeKit, MailKit, DnsClient.NET |
| Bảng mã ký tự cũ | `System.Text.Encoding.CodePages` của .NET | windows-1258, windows-1252, iso-8859-x, GB2312, Big5, Shift_JIS… cho thư cũ |
| Lọc HTML | **HtmlSanitizer** (MIT) phía máy chủ, **DOMPurify** phía trình duyệt | |
| Quét tệp | **Tự viết** — `BlueMail.Scanner` (mục 2.6.4) | Không dùng ClamAV; có interface để về sau cắm thêm bộ quét thương mại |
| Frontend | **React 18 + TypeScript + Vite**, Ant Design 5 (gói ngôn ngữ đổi theo người dùng), TanStack Query, Zustand, react-i18next | |
| Trình soạn thư | **TipTap** (lõi MIT) | Chỉ dùng phần mở rộng miễn phí |
| Xem PDF | pdf.js | |
| Log | Serilog → tệp + PostgreSQL, JSON có cấu trúc; bật `Serilog.Debugging.SelfLog` | |
| Kiểm thử | xUnit, **FluentAssertions 7.2.0** (≥ 8 là thương mại), Testcontainers, SharpFuzz, Vitest, Playwright, k6 | |
| MediatR | **12.5.0** | Bản Apache-2.0 cuối; không nâng lên 13+ |
| Triển khai | Docker + Docker Compose, Caddy làm reverse proxy HTTPS | Cổng thư (25/465/587/993) đi thẳng vào dịch vụ, không qua Caddy |

Mọi gói NuGet/npm: kiểm giấy phép, ghi vào `docs/09-giay-phep-thanh-phan.md`. Có phép thử quét
`Directory.Packages.props` và `package.json`, gói ngoài danh sách đã duyệt thì đỏ.

---

## 2. KIẾN TRÚC

```
 Internet (máy chủ thư khác)            Web BlueMail (React)          Outlook / Thunderbird / điện thoại
        │ SMTP :25                              │ HTTPS                     │ IMAP :993, SMTP nộp thư :465/:587
        ▼                                       ▼                           ▼
 ┌──────────────┐                     ┌──────────────────┐          ┌──────────────┐   ┌──────────────┐
 │ BlueMail.Mx  │                     │  BlueMail.Api    │          │ BlueMail.Imap│   │BlueMail.     │
 │ SMTP nhận vào│                     │  REST + SignalR  │          │ (Phase 11)   │   │Submission    │
 │ SPF/DKIM/    │                     └────────┬─────────┘          └──────┬───────┘   │SMTP AUTH     │
 │ DMARC/ARC,   │                              │                           │           └──────┬───────┘
 │ chấm rác,    │                              │                           │                  │
 │ quét tệp     │                              ▼                           ▼                  │
 └──────┬───────┘                ┌──────────────────────────────────────────────────┐         │
        │ giao thư               │  IMailStore (BlueMail.MailStore — thư viện dùng   │◄────────┘
        └───────────────────────►│  chung): thư, hội thoại, nhãn, cờ, modseq, UID,  │  thư gửi đi
                                 │  bộ lọc người dùng, tìm kiếm, sự kiện            │
                                 └───────┬───────────────────────┬──────────────────┘
                                         │                       │ thư cần gửi ra
                                         ▼                       ▼
                       PostgreSQL (siêu dữ liệu, chỉ mục)   ┌──────────────────────┐
                       S3 (nội dung thư gốc, mã hoá)        │ BlueMail.Outbound    │──► SMTP :25 tới
                       Valkey (sự kiện, đệm)                │ hàng đợi, ký DKIM,   │    máy chủ người nhận
                                                            │ thử lại, DSN, IP pool│
                                                            └──────────────────────┘
          BlueMail.Identity (BlueID)  ·  BlueMail.Worker (Hangfire)
```

Mỗi khối trong sơ đồ là một **tiến trình riêng** (container riêng), cùng dùng thư viện `BlueMail.MailStore`
và `BlueMail.Domain`. Tách tiến trình để: cổng 25 bị tấn công không kéo đổ web; mở rộng riêng từng phần.

### 2.1. Nhận thư (BlueMail.Mx) — đường đi một lá thư đến

1. **Kết nối**: giới hạn số kết nối đồng thời theo IP và tổng; tra PTR; tra danh sách đen DNS (DNSBL) theo
   cấu hình; lời chào trễ ngắn để bắt máy phát rác nói trước (pre-greet).
2. **Phiên SMTP** (RFC 5321) với các phần mở rộng: `PIPELINING`, `SIZE`, `8BITMIME`, `SMTPUTF8` (RFC 6531),
   `STARTTLS` (RFC 3207), `ENHANCEDSTATUSCODES` (RFC 2034), `CHUNKING`/`BDAT` (RFC 3030). Giới hạn độ dài
   dòng lệnh, số người nhận mỗi thư, cỡ thư, thời gian chờ từng bước.
3. **`RCPT TO`**: chỉ nhận người nhận thuộc tên miền đã xác minh của một tenant **và** tồn tại (người dùng,
   bí danh, nhóm, catch-all). Không tồn tại → `550 5.1.1` ngay, **không bao giờ nhận rồi mới trả lại**
   (backscatter). Không bao giờ chuyển tiếp thư cho tên miền không thuộc hệ thống (không relay mở).
4. **Sau `DATA`**: đọc thư theo luồng ra tệp tạm (không đọc cả thư vào RAM), giới hạn cỡ.
5. **Xác thực người gửi**: SPF (RFC 7208, tự viết bộ đánh giá: `include`, `a`, `mx`, `ip4`, `ip6`,
   `exists`, `redirect`, `exp`, macro; **giới hạn 10 lượt tra DNS và 2 lượt void lookup**), DKIM (RFC 6376,
   `BlueMail.Dkim`), DMARC (RFC 7489, tự viết: căn chỉnh relaxed/strict, chính sách `p`/`sp`/`pct`, tìm
   tên miền tổ chức bằng **Public Suffix List** đóng gói sẵn và cập nhật định kỳ), ARC (RFC 8617, `BlueMail.Dkim` —
   để tin thư đi qua danh sách thư). Ghi kết quả vào tiêu đề `Authentication-Results` (RFC 8601).
6. **Quét tệp đính kèm** (`BlueMail.Scanner`, mục 2.6.4) và **chấm điểm rác** (mục 2.3).
7. **Quyết định**: từ chối ngay trong phiên SMTP (`550`/`554`) khi chắc chắn (virus, DMARC `p=reject` thất
   bại, điểm rác rất cao) — **từ chối trong phiên, không nhận rồi bỏ**; nhận vào Thư rác khi nghi ngờ; nhận
   vào cách ly khi chính sách tenant yêu cầu.
8. **Giao thư**: với **từng** người nhận, gọi `IMailStore.DeliverAsync`: lưu nội dung gốc một lần lên S3,
   tạo bản ghi thư trong hộp thư người nhận, ghép hội thoại, áp quy tắc định tuyến của tenant → bộ lọc của
   người dùng → phân loại tab → trả lời tự động → chuyển tiếp. **Chỉ trả `250` sau khi đã lưu bền vững cho
   mọi người nhận** (ghi CSDL đã commit và đối tượng S3 đã ghi xong). Nhóm/danh sách phân phối được mở rộng
   thành người nhận ở bước này.
9. Phát sự kiện → SignalR báo web, IMAP IDLE báo phần mềm thư.

### 2.2. Gửi thư (Submission + Outbound)

1. Thư đến từ web (API) hoặc từ phần mềm thư qua **SMTP nộp thư** (RFC 6409, cổng 587 với `STARTTLS`
   bắt buộc, cổng 465 TLS ngầm định theo RFC 8314, `AUTH PLAIN`/`LOGIN` chỉ sau TLS, và `XOAUTH2`/
   `OAUTHBEARER` bằng thẻ BlueID).
2. Kiểm: người gửi có quyền dùng địa chỉ `From` (chính chủ, bí danh, gửi thay); hạn mức gửi; cỡ thư; số người
   nhận; chính sách tenant (chặn đuôi tệp, chân thư bắt buộc).
3. Lưu bản trong nhãn **Đã gửi** của người gửi (qua `IMailStore`). Người nhận cùng hệ thống → giao nội bộ thẳng
   qua `IMailStore.DeliverAsync` (vẫn chạy bộ lọc của người nhận), không đi vòng ra Internet.
4. Người nhận bên ngoài → ký **DKIM** bằng khoá của tên miền gửi (`BlueMail.Dkim`, `rsa-sha256` 2048-bit
   qua `System.Security.Cryptography`; không ký `ed25519-sha256` — xem nguyên tắc 8) → đưa vào **hàng đợi gửi đi** (2.4).

### 2.3. Chấm điểm thư rác (tự viết)

- **Bộ luật có trọng số**, mỗi luật có mã, mô tả (dịch được), điểm: kết quả SPF/DKIM/DMARC, DNSBL/URIBL, PTR
  thiếu hoặc không khớp, `HELO` vô lý, tiêu đề sai chuẩn (thiếu `Date`, `Message-ID` lạ), `From` hiển thị giả
  dạng tên miền khác, tỉ lệ HTML/chữ, liên kết có chữ hiển thị khác địa chỉ, đuôi tệp nguy hiểm, tên miền mới
  đăng ký (nếu có nguồn dữ liệu), ký tự đồng hình Unicode trong tên miền.
- **Bộ phân loại Bayes** (tự viết; tách từ không phụ thuộc ngôn ngữ: chuẩn hoá NFC, ranh giới từ theo
  Unicode UAX #29, bigram cho chữ không có khoảng trắng, thêm bản đã bỏ dấu cho chữ Latin): mô hình chung + mô hình theo tenant + mô hình theo người
  dùng; học từ nút "Báo cáo thư rác"/"Không phải thư rác".
- **Danh tiếng**: người gửi người dùng đã từng trả lời / có trong danh bạ thì trừ điểm; tên miền người gửi
  lần đầu thấy thì cộng nhẹ.
- **Greylisting** (tuỳ chọn theo tenant, mặc định tắt).
- Ngưỡng: `≥ reject_threshold` từ chối trong phiên; `≥ spam_threshold` vào Thư rác; ngưỡng cấu hình theo tenant.
- Mỗi thư lưu **bảng điểm chi tiết** (luật nào cộng bao nhiêu) — người dùng xem được qua "Vì sao thư này ở
  Thư rác?", quản trị viên xem được ở tìm vết thư.
- ⚠️ **Giấy phép dữ liệu**: nhiều DNSBL (ví dụ Spamhaus) **cấm dùng thương mại miễn phí** quá ngưỡng; Phase 0
  liệt kê nguồn nào dùng được, nguồn nào phải trả phí, ghi vào `docs/00`. Bộ chấm điểm phải chạy được khi
  không có DNSBL nào.
- Đo chất lượng: tập thư mẫu có nhãn (tự gom + bộ mẫu công khai có giấy phép phù hợp), báo cáo tỉ lệ bắt
  đúng / bắt nhầm sau mỗi lần đổi luật. **Bắt nhầm thư thật nguy hiểm hơn lọt thư rác**: mục tiêu bắt nhầm
  < 0,1 %.

### 2.4. Hàng đợi gửi đi (BlueMail.Outbound)

- Hàng đợi **trong PostgreSQL** (`mta.outbound_queue`), lấy việc bằng `SELECT … FOR UPDATE SKIP LOCKED`,
  nhiều tiến trình gửi chạy song song an toàn. Không dùng Hangfire cho việc này (cần kiểm soát theo tên miền
  đích, theo IP, theo lịch thử lại riêng).
- Mỗi người nhận một trạng thái riêng (thư gửi 10 người, 9 người nhận xong, 1 người tạm lỗi → chỉ thử lại 1).
- Tra MX (RFC 5321 mục 5: theo độ ưu tiên, không có MX thì dùng A/AAAA, MX rỗng `.` theo RFC 7505 thì trả lại
  ngay); thử lần lượt các MX; IPv4 và IPv6 theo cấu hình.
- **TLS khi gửi**: thử `STARTTLS` luôn; tôn trọng **MTA-STS** (RFC 8461), và **DANE** (RFC 7672) từ Phase 12 khi `BlueMail.Dns` đã kiểm được DNSSEC — chính sách yêu cầu TLS mà không thoả thì không gửi văn bản trần; ghi báo cáo **TLS-RPT**
  (RFC 8460).
- Thử lại khi lỗi tạm (`4xx`, mất kết nối) theo lịch giãn dần (ví dụ 5 phút, 15 phút, 30 phút, 1 giờ, 2 giờ,
  4 giờ… tới **5 ngày**); báo trễ cho người gửi sau 4 giờ; hết hạn thì trả **DSN** (RFC 3464) bằng tiếng Anh
  kèm ngôn ngữ của người gửi. Lỗi vĩnh viễn (`5xx`) → DSN ngay.
- Giới hạn đồng thời và tốc độ **theo tên miền đích** (Gmail, Outlook, Yahoo… có giới hạn riêng, cấu hình
  được), theo IP gửi; tự giảm tốc khi nhận `421`/`4.7.x` kiểu "gửi quá nhanh".
- **Dải IP**: gán IP gửi theo hạng tenant (dùng thử / trả phí / IP riêng); **làm ấm IP mới** theo lịch tăng
  dần; ràng buộc mỗi IP một tên `HELO` khớp PTR.
- **Danh sách chặn gửi** (suppression): địa chỉ trả lại cứng thì ngừng gửi tới, có thời hạn.
- **Máy chủ chuyển tiếp ngoài** (smarthost) cấu hình được cho bản cài riêng hoặc khi IP chưa đủ uy tín.
- Xử lý **thư trả lại không đồng bộ** đến sau (bounce về địa chỉ VERP) và **báo cáo khiếu nại** (ARF, RFC
  5965) → cập nhật danh sách chặn gửi, cộng điểm lạm dụng cho người gửi.
- Mọi lượt gửi ghi vào `admin.mail_log` cho tìm vết thư.

### 2.5. Kho thư (BlueMail.MailStore)

- **Nội dung gốc RFC 5322** của mỗi thư lưu trên S3, **mã hoá AES-256-GCM theo khoá của tenant** (khoá tenant
  được bọc bằng khoá chủ từ biến môi trường), khoá đối tượng theo băm SHA-256 của bản rõ trong phạm vi
  tenant — một thư gửi 50 người cùng tenant chỉ lưu **một** lần.
- PostgreSQL giữ: siêu dữ liệu đã phân tích, cấu trúc phần MIME (để lấy riêng một tệp đính kèm không phải tải
  cả thư), đoạn trích, kết quả xác thực, điểm rác, nhãn, cờ, hội thoại, vector tìm kiếm.
- **Ghép hội thoại**: theo `References`/`In-Reply-To` (RFC 5322) trước, rồi theo tiêu đề đã chuẩn hoá (bỏ
  `Re:`, `Fwd:`, `TL:`, `Trả lời:`, `Chuyển tiếp:`…) trong khoảng thời gian giới hạn — như Gmail. Ghép theo
  **hộp thư từng người**, không ghép chéo người dùng.
- **Nhãn kiểu Gmail**: một thư nhiều nhãn. Nhãn hệ thống: `INBOX`, `SENT`, `DRAFTS`, `SPAM`, `TRASH`,
  `STARRED`, `IMPORTANT`, `SNOOZED`, `SCHEDULED`, các tab phân loại. "Tất cả thư" = mọi thư không ở `SPAM`/
  `TRASH`.
- **Sẵn cho đồng bộ ngay từ Phase 1** (dù IMAP làm ở Phase 11, thêm về sau là sửa cả CSDL): mỗi hộp thư có
  `highest_modseq`; mỗi thư có `modseq` tăng ở mọi thay đổi; mỗi cặp (nhãn, thư) có **UID IMAP** tăng dần,
  mỗi nhãn có `uid_validity` và `uid_next`. Cấp UID và modseq bằng thao tác nguyên tử trong CSDL.
- **Hạn mức dung lượng**: tính lại từ tổng cỡ thư của người dùng bằng truy vấn (không cộng dồn tay); kiểm khi
  nhận thư (`452 4.2.2` khi đầy) và khi gửi.
- **Không có cột "đếm" chép sẵn nào** (số chưa đọc, số thư của nhãn) trừ khi đo được là cần; nếu cần thì
  **tính lại từ nguồn** trong cùng giao dịch với thay đổi, và có phép thử đối soát.

### 2.6. Thư viện nền tự viết

Bốn thư viện dưới đây là **dự án .NET độc lập**, không phụ thuộc gì vào phần còn lại của BlueMail (không EF
Core, không MediatR), để kiểm thử riêng và về sau tách thành gói dùng cho sản phẩm khác. Mỗi thư viện có
`README.md` liệt kê RFC, mục nào đã làm, mục nào chưa.

#### 2.6.1. `BlueMail.Mime` — phân tích và dựng thư

- **Phân tích theo luồng**: đọc từ `Stream`/`PipeReader`, không bao giờ nạp cả thư vào bộ nhớ; tệp đính kèm lớn
  đọc ra luồng con. Trả về cây phần MIME có vị trí byte của từng phần (để kho thư lấy riêng một tệp đính kèm
  từ S3 bằng `Range`).
- **Tiêu đề**: gấp/mở dòng, địa chỉ (nhóm, tên hiển thị có dấu ngoặc kép, chú thích, địa chỉ quốc tế hoá
  RFC 6532), ngày (RFC 5322 kèm các dạng cũ RFC 822 hay gặp), `Message-ID`/`References`, từ mã hoá RFC 2047
  (B và Q, nhiều bảng mã, từ mã hoá bị cắt giữa ký tự nhiều byte), tham số RFC 2231 (tên tệp ngoài ASCII dài
  bị chia nhiều đoạn).
- **Thân**: `base64`, `quoted-printable`, `7bit`/`8bit`/`binary`; `multipart/*` lồng nhau (giới hạn độ sâu);
  `message/rfc822` lồng; `format=flowed` (RFC 3676); `Content-Disposition` (RFC 2183); `cid:` (RFC 2392);
  `message/delivery-status` (để đọc DSN); `text/calendar` (đọc đủ cho thẻ lời mời).
- **Bảng mã**: UTF-8 và mọi bảng mã `System.Text.Encoding.CodePages` hỗ trợ; nhãn bảng mã sai/thiếu thì đoán
  có kiểm soát (thử UTF-8 hợp lệ trước, rồi theo nhãn, rồi windows-1252) — không bao giờ ném lỗi làm mất thư.
- **Thư sai chuẩn ngoài đời phải đọc được**: thiếu ranh giới đóng, ranh giới sai, dòng trần LF, 8-bit trong
  tiêu đề không khai báo, base64 có ký tự rác, tiêu đề lặp. Mỗi kiểu sai có một thư mẫu trong bộ kiểm thử.
- **Dựng thư**: cây MIME → byte đúng chuẩn, dòng ≤ 998 byte, CRLF, chọn mã hoá truyền tải tự động, RFC 2047/2231
  cho mọi chữ ngoài ASCII, ranh giới ngẫu nhiên không va chạm.
- **HTML → chữ thuần** (cho đoạn trích, chỉ mục tìm kiếm, phần `text/plain` khi soạn): tự viết bộ tách thẻ đơn
  giản đủ dùng; **lọc HTML an toàn để hiển thị vẫn dùng HtmlSanitizer** (mục 1) — đó là thư viện bảo mật, không
  thuộc phạm vi tự viết này.
- **Giới hạn cứng** cấu hình được: cỡ tiêu đề, số tiêu đề, độ sâu lồng, số phần, độ dài dòng.

#### 2.6.2. `BlueMail.Dns` — tra DNS

- Máy khách DNS đệ quy-gọi (stub) tới các máy chủ phân giải cấu hình được: UDP với EDNS0, chuyển TCP khi bị cắt
  (`TC`), thời gian chờ và thử lại, ID và cổng nguồn ngẫu nhiên (chống giả mạo), kiểm câu trả lời khớp câu hỏi.
- Bản ghi: A, AAAA, MX, TXT (ghép nhiều chuỗi), PTR, CNAME (theo chuỗi, có giới hạn), NS, SOA, SRV, TLSA,
  CAA; phân biệt NXDOMAIN với không có bản ghi (cần cho SPF "void lookup" và MX rỗng).
- Bộ đệm theo TTL (có trần và sàn), đệm cả kết quả âm (RFC 2308), giới hạn kích thước bộ đệm.
- **DNSSEC** (Phase 12, cho DANE): tự kiểm chuỗi tin cậy từ khoá gốc (RFC 4033–4035, thuật toán 8 và 13 qua
  `System.Security.Cryptography`), NSEC/NSEC3 chứng minh không tồn tại. Trước khi có, mọi thứ cần DNSSEC phải
  tắt và ghi rõ.

#### 2.6.3. `BlueMail.Dkim` — DKIM và ARC

- Ký và kiểm DKIM (RFC 6376): chuẩn hoá `simple`/`relaxed` cho tiêu đề và thân, `bh=`, `h=` (ký cả tiêu đề
  chưa có để chống chèn thêm — "oversigning" `From`, `Subject`, `To`, `Date`), bỏ qua `l=` khi kiểm (coi như
  không có `l=`), `x=` hết hạn, khoá ≥ 1024 bit khi kiểm, 2048 bit khi ký (RFC 8301), từ chối `rsa-sha1`.
- ARC (RFC 8617): kiểm chuỗi `ARC-Seal`/`ARC-Message-Signature`/`ARC-Authentication-Results`; ký ARC khi
  BlueMail chuyển tiếp thư hoặc mở rộng nhóm có người nhận bên ngoài.
- Đọc khoá công khai qua `BlueMail.Dns`, đệm theo TTL.

#### 2.6.4. `BlueMail.Scanner` — quét tệp đính kèm

⚠️ **Nói thẳng giới hạn**: giá trị chính của phần mềm diệt virus là **kho mẫu virus được cập nhật hằng giờ**
bởi đội phân tích chuyên nghiệp. Tự viết bộ máy quét thì làm được; tự có kho mẫu thì không. Vì vậy
`BlueMail.Scanner` **không cố làm phần mềm diệt virus**, mà chặn theo **loại tệp, cấu trúc và hành vi** —
chính là lớp chặn được phần lớn mã độc qua thư trong thực tế (Gmail cũng chặn thẳng theo loại tệp):

1. **Nhận diện loại tệp bằng chữ ký byte**, không tin đuôi tệp hay `Content-Type`. Phát hiện đuôi kép
   (`hoadon.pdf.exe`), ký tự đảo chiều Unicode (RLO) trong tên tệp, đuôi không khớp nội dung.
2. **Chặn loại nguy hiểm** (danh sách cấu hình được, mặc định như Gmail): tệp thực thi Windows/Linux/macOS,
   script (`.js`, `.vbs`, `.ps1`, `.bat`, `.cmd`, `.hta`, `.wsf`…), `.lnk`, `.iso`/`.img`/`.vhd`, `.msi`,
   `.jar`, `.scr`, `.chm`, `.reg`… — **kể cả khi nằm trong tệp nén**.
3. **Mở tệp nén** (ZIP qua `System.IO.Compression`; tự đọc thư mục của RAR/7z/TAR/GZ ở mức liệt kê tên tệp):
   giới hạn độ sâu lồng, tổng dung lượng giải nén, tỉ lệ nén (chống bom nén), số tệp. Tệp nén có mật khẩu: không
   quét được nội dung → theo chính sách tenant (mặc định: cảnh báo đỏ, quản trị viên chọn chặn hẳn).
4. **Tài liệu Office**: OOXML (zip) có `vbaProject.bin`, OLE2 (`.doc/.xls`) có luồng VBA, liên kết đối tượng
   ngoài, DDE, mẫu từ xa (`attachedTemplate` trỏ ra Internet) → đánh dấu macro/nguy hiểm theo chính sách.
5. **PDF**: hành động `/JavaScript`, `/JS`, `/Launch`, `/OpenAction` gọi script, `/EmbeddedFile` thực thi, `/URI`
   tới tên miền xấu.
6. **HTML/SVG đính kèm**: script, "HTML smuggling" (blob nhúng base64 tự tải), form gửi mật khẩu ra ngoài.
7. **Danh sách băm mã độc**: SHA-256 của tệp so với danh sách tệp độc đã biết — nguồn dữ liệu và **giấy phép
   dùng thương mại** chốt ở Phase 0 (Z7), cập nhật định kỳ, tra trong bộ nhớ.
8. **Bộ luật mẫu byte** tự viết (cú pháp đơn giản: chuỗi, hex có ký tự đại diện, điều kiện), để đội vận hành thêm
   luật nhanh khi có đợt tấn công mới.
- Kết quả: `CLEAN` / `SUSPICIOUS` (cảnh báo, theo chính sách) / `MALICIOUS` (chặn) kèm **mã lý do** và mô tả dịch được.
- Interface `IFileScanner`: nhiều bộ quét chạy nối tiếp, kết quả nặng nhất thắng — sau này cắm thêm bộ quét
  thương mại (ví dụ của Bkav) **không phải sửa chỗ gọi**. Hiện tại chỉ có một bộ cài đặt là `BlueMail.Scanner`;
  không viết sẵn bộ cài đặt rỗng.
- Đo chất lượng: bộ tệp mẫu sạch (tài liệu thật nhiều ngôn ngữ, Office có/không macro hợp lệ) và bộ mẫu độc hại
  **vô hại hoá** (tệp kiểm thử EICAR, tệp dựng tay có đúng cấu trúc nguy hiểm). Không đưa mã độc thật vào kho mã.

#### 2.6.5. Máy khách IMAP (cho công cụ chuyển thư sang)

Dùng chung bộ phân tích cú pháp IMAP với máy chủ (`BlueMail.Protocols.Imap`). Chỉ cần: TLS, `LOGIN`,
`AUTHENTICATE PLAIN`, `CAPABILITY`, `LIST` (kèm `SPECIAL-USE`), `SELECT`/`EXAMINE`, `UID SEARCH`, `UID FETCH`
(`FLAGS`, `INTERNALDATE`, `RFC822.SIZE`, `BODY.PEEK[]`, và `X-GM-LABELS` khi nguồn là Gmail), `LOGOUT`;
đọc literal theo luồng.

### 2.7. Cấu trúc repo

```
bluemail/
├── BlueMail.slnx
├── Directory.Build.props, Directory.Packages.props
├── CLAUDE.md                        # hướng dẫn làm việc, cập nhật sau mỗi phase
├── README.md
├── deploy/
│   ├── docker-compose.yml           # bản chạy thật
│   ├── docker-compose.dev.yml       # dev: + Postfix "doitac.test", CoreDNS cho ba tên miền test
│   ├── caddy/Caddyfile
│   ├── coredns/                     # vùng DNS test: MX, SPF, DKIM, DMARC, PTR
│   ├── postgres/init/
│   └── .env.example                 # chú thích từng biến (tiếng Anh)
├── src/
│   ├── BlueMail.Domain/
│   ├── BlueMail.Application/        # Features/<TínhNăng>/{Command,Query,Handler,Validator,Dto}
│   ├── BlueMail.MailStore/          # IMailStore: kho thư, hội thoại, nhãn, UID/modseq, tìm kiếm, sự kiện
│   ├── BlueMail.Protocols.Smtp/     # bộ phân tích + máy trạng thái SMTP dùng chung cho Mx và Submission
│   ├── BlueMail.Protocols.Imap/     # bộ phân tích + máy khách (Phase 10), máy chủ (Phase 11)
│   ├── BlueMail.Mime/               # tự viết: phân tích/dựng MIME (2.6.1)
│   ├── BlueMail.Dns/                # tự viết: máy khách DNS (2.6.2)
│   ├── BlueMail.Dkim/               # tự viết: DKIM, ARC (2.6.3)
│   ├── BlueMail.Scanner/            # tự viết: quét tệp đính kèm (2.6.4)
│   ├── BlueMail.Auth/               # SPF, DMARC, Public Suffix List
│   ├── BlueMail.Spam/               # bộ luật, Bayes, danh tiếng
│   ├── BlueMail.Infrastructure/     # EF Core, S3, mã hoá, Valkey
│   ├── BlueMail.Identity/           # BlueID
│   ├── BlueMail.Api/
│   ├── BlueMail.Mx/                 # tiến trình SMTP :25
│   ├── BlueMail.Submission/         # tiến trình SMTP :465/:587
│   ├── BlueMail.Outbound/           # tiến trình hàng đợi gửi đi
│   ├── BlueMail.Imap/               # tiến trình IMAP :993 (Phase 11)
│   └── BlueMail.Worker/             # Hangfire
├── tests/
│   ├── BlueMail.UnitTests/
│   ├── BlueMail.IntegrationTests/   # Testcontainers PostgreSQL + S3
│   ├── BlueMail.InteropTests/       # gửi/nhận với Postfix thật, kiểm bằng công cụ của người khác
│   ├── BlueMail.FuzzTests/          # SharpFuzz cho SMTP/IMAP/MIME/DNS/SPF/Scanner
│   ├── BlueMail.Corpus/             # bộ thư mẫu thật và thư sai chuẩn cho kiểm thử MIME (2.6.1)
│   └── load/                        # k6 (HTTP) + công cụ bắn SMTP
├── web/                             # React SPA
├── scripts/                         # install, backup, restore, ci (.sh + .ps1)
└── docs/
```

---

## 3. PHASE 0 — DỰNG NỀN KIỂM THỬ VÀ CHỐT QUYẾT ĐỊNH

Không viết tính năng ở phase này. Mục tiêu: có môi trường để **chứng minh được** từng phase sau đúng chuẩn.

| # | Việc | Kết quả |
|---|---|---|
| Z1 | `docker-compose.dev.yml`: PostgreSQL, S3, Valkey, **CoreDNS** phục vụ vùng `bluemail.test`, `acme.test`, `doitac.test` (MX, A, PTR, SPF, DKIM, DMARC), **Postfix + OpenDKIM** làm `doitac.test` | Hai bên gửi được thư cho nhau trong mạng Docker mà không cần Internet |
| Z2 | Công cụ đối chứng chạy trong container: `swaks` (gửi SMTP), Python `dkimpy` + `authheaders` + `checkdmarc` (kiểm xác thực), `imaptest` của Dovecot (kiểm IMAP, dùng ở Phase 11), Python `mailbox`/`imaplib`/`email` và `dnspython` (đối chứng cho `BlueMail.Mime`, máy khách IMAP, `BlueMail.Dns`); **ClamAV chỉ trong container kiểm thử** để so kết quả với `BlueMail.Scanner` | Lệnh chạy được ghi trong `docs/06`. Không thứ nào trong số này nằm trong image chạy thật |
| Z3 | Chọn kho S3 (MinIO / Garage / SeaweedFS) theo giấy phép và tình trạng phát hành | Ghi `docs/00` |
| Z4 | Danh sách DNSBL/URIBL được phép dùng cho sản phẩm thương mại và chi phí | Ghi `docs/00` |
| Z5 | Ước lượng tải: số thư nhận/gửi mỗi ngày cho 1 máy chủ 8 nhân/16 GB, dung lượng S3/người dùng | Ghi `docs/00`, dùng làm ngưỡng cho phép thử tải |
| Z6 | Kiểm khả năng mở cổng 25 ra ngoài của hạ tầng định thuê (nhiều nhà cung cấp đám mây chặn cổng 25 mặc định) và khả năng đặt PTR | Ghi `docs/00`; không đạt thì báo chủ sản phẩm ngay |
| Z7 | Nguồn danh sách băm mã độc và luật cho `BlueMail.Scanner`: giấy phép dùng thương mại, tần suất cập nhật, cách tải | Ghi `docs/00`; không có nguồn hợp lệ thì bộ quét chạy chỉ với lớp 1–6 của 2.6.4 |
| Z8 | **Bộ thư mẫu** cho `BlueMail.Mime`: thư thật đã xin phép hoặc tự tạo từ Outlook, Gmail, Thunderbird, Apple Mail, Yahoo, các hệ thống gửi thông báo tự động; **nhiều ngôn ngữ và bảng mã** (UTF-8, Latin có dấu, Kirin, CJK kể cả ISO-2022-JP/GB2312/Big5, Ả Rập, emoji); thư sai chuẩn tự dựng; nguồn công khai có giấy phép rõ | Ghi `docs/00`, đưa vào `tests/BlueMail.Corpus`, bỏ thông tin cá nhân |

---

## 4. MÔ HÌNH DỮ LIỆU POSTGRESQL (CSDL `bluemail`)

Mọi bảng: `id uuid default gen_random_uuid()` (trừ bảng ghi chú khác), `tenant_id` (trừ bảng của chính
tenant), `created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at` (xoá mềm). Tên bảng `snake_case`
số nhiều. Mọi chỉ mục duy nhất có `WHERE deleted_at IS NULL`. Mọi luật "chỉ một …" là **ràng buộc duy nhất ở
CSDL**. Bảng thư lớn (`mail.messages`, `mail.message_labels`) **phân vùng theo `tenant_id`** (hash) từ đầu.

```
tenant.tenants              -- code, name, plan, status(ACTIVE|SUSPENDED|TRIAL), storage_quota_bytes,
                            -- user_limit, outbound_daily_limit, ip_pool_id, spam_settings(jsonb), default_locale,
                            -- default_time_zone, data_region,
                            -- encryption_key_wrapped, branding(jsonb)
tenant.domains              -- domain (duy nhất toàn hệ thống), is_primary, verification_token, verified_at,
                            -- mx_ok, spf_ok, dkim_ok, dmarc_ok, mta_sts_ok, last_checked_at, catch_all_target
tenant.dkim_keys            -- domain_id, selector, algorithm(RSA2048|ED25519), private_key_encrypted,
                            -- public_key, status(PENDING|ACTIVE|RETIRING|RETIRED), activated_at, retire_at
tenant.domain_dns_checks    -- domain_id, checked_at, record_type, expected, actual, ok

identity.users              -- tenant_id, primary_email (duy nhất), display_name, given_name, family_name,
                            -- password_hash, status(ACTIVE|SUSPENDED|DELETED), is_tenant_admin, quota_bytes,
                            -- locale, time_zone (IANA, mặc định theo tenant), hour_cycle, week_start, two_factor_enabled,
                            -- must_change_password, last_login_at, failed_login_count, locked_until,
                            -- org_unit_id, imap_enabled, security_stamp
identity.user_aliases       -- user_id, email (duy nhất toàn hệ thống), can_send_as
identity.org_units          -- tenant_id, parent_id, name
identity.groups             -- tenant_id, email (duy nhất), name, type(DISTRIBUTION|SECURITY),
                            -- who_can_post(ANYONE|DOMAIN|MEMBERS|MANAGERS), allow_external, moderated
identity.group_members      -- group_id, user_id | external_email, role(OWNER|MANAGER|MEMBER)
identity.shared_mailboxes   -- tenant_id, email, name; shared_mailbox_members(user_id, can_read, can_send_as,
                            -- can_send_on_behalf)
identity.app_passwords      -- user_id, name, secret_hash, last_used_at, revoked_at
identity.two_factor         -- user_id, totp_secret_encrypted, recovery_codes_hash(jsonb)
identity.sessions           -- user_id, device, ip, user_agent, created_at, last_seen_at, revoked_at
identity.login_events       -- user_id, protocol(WEB|IMAP|SMTP), ip, user_agent, result, reason, occurred_at,
                            -- is_suspicious

mail.mailboxes              -- (mỗi người dùng/hộp thư dùng chung một dòng) owner_id, highest_modseq,
                            -- used_bytes (TÍNH LẠI, xem 2.5)
mail.labels                 -- mailbox_id, system_key null (INBOX, SENT…), name, parent_id, color_bg,
                            -- color_text, show_in_list, show_in_message_list, uid_validity, uid_next,
                            -- imap_subscribed       UNIQUE(mailbox_id, parent_id, name)
mail.blobs                  -- (id = sha256 trong phạm vi tenant) tenant_id, object_key, size, created_at
                            -- PK(tenant_id, sha256)
mail.messages               -- mailbox_id, blob_sha256, thread_id, rfc_message_id, in_reply_to,
                            -- references(text[]), subject, normalized_subject, from_addr, from_name,
                            -- to_addrs(jsonb), cc_addrs(jsonb), bcc_addrs(jsonb, chỉ với thư mình gửi),
                            -- reply_to(jsonb), sent_at, received_at, size, snippet, has_attachments,
                            -- mime_structure(jsonb), seen, flagged, answered, forwarded, draft,
                            -- keywords(text[]), spam_score, spam_report(jsonb), auth_results(jsonb),
                            -- list_id, list_unsubscribe(jsonb), category, is_important, modseq,
                            -- search_vector(tsvector), legal_hold_count (TÍNH LẠI), expunged_at
mail.message_labels         -- message_id, label_id, imap_uid, modseq   PK(message_id, label_id)
                            -- UNIQUE(label_id, imap_uid)
mail.threads                -- mailbox_id, root_message_id, normalized_subject, last_message_at, muted
mail.attachments            -- message_id, part_path, file_name, content_type, size, content_id, is_inline,
                            -- virus_status(CLEAN|INFECTED|UNSCANNED), sha256
mail.label_changes          -- (nhật ký để IMAP trả lời QRESYNC/VANISHED và để web đồng bộ tăng dần)
                            -- label_id, imap_uid, modseq, change(ADDED|REMOVED|FLAGS), occurred_at

mail.signatures             -- user_id, name, html, is_default_new, is_default_reply, alias_id
mail.templates              -- user_id, name, subject, html
mail.outgoing               -- user_id, draft_message_id, from_address, status(UNDO_WINDOW|SCHEDULED|
                            -- SUBMITTING|SUBMITTED|CANCELLED|FAILED), send_at, submitted_at, error
                            -- UNIQUE(draft_message_id) WHERE status IN ('UNDO_WINDOW','SCHEDULED','SUBMITTING')
mail.snoozes                -- user_id, thread_id, until_at, status   UNIQUE(user_id, thread_id) WHERE status='ACTIVE'
mail.filters                -- user_id, criteria(jsonb), actions(jsonb), sort_order, is_enabled
mail.blocked_senders        -- user_id, address_or_domain
mail.vacation               -- user_id, enabled, start_at, end_at, subject, html, only_contacts, only_domain,
                            -- reply_interval_days
mail.vacation_replies       -- user_id, sender, last_replied_at   UNIQUE(user_id, sender)
mail.forwarding             -- user_id, target_email, verified_at, verification_code_hash, keep_copy
mail.user_settings          -- user_id, settings(jsonb): page_size, density, reading_pane, conversation_view,
                            -- undo_send_seconds(5|10|20|30), default_reply, external_images(ASK|ALWAYS),
                            -- keyboard_shortcuts, inbox_type, categories(jsonb), theme, send_and_archive,
                            -- auto_advance, desktop_notifications
mail.trusted_image_senders  -- user_id, address_or_domain
mail.category_rules         -- tenant_id null = mặc định hệ thống; match(jsonb) → category
mail.large_attachments      -- user_id, object_key, file_name, size, sha256, expires_at, download_count
mail.confidential_messages  -- user_id, message_id, token_hash, expires_at, require_otp, revoked_at

mta.outbound_messages       -- tenant_id, sender_user_id, blob_sha256, envelope_from (VERP), dkim_domain,
                            -- ip_pool_id, created_at, priority
mta.outbound_recipients     -- outbound_message_id, rcpt, domain, status(QUEUED|DEFERRED|DELIVERED|BOUNCED|
                            -- EXPIRED|SUPPRESSED), attempts, next_attempt_at, last_mx, last_ip,
                            -- last_response, delivered_at, dsn_sent_at
                            -- chỉ mục (status, next_attempt_at) cho SKIP LOCKED
mta.ip_pools                -- name, ips(jsonb: ip, helo, warmup_started_at, daily_cap)
mta.destination_limits      -- domain_pattern, max_connections, max_messages_per_minute
mta.suppressions            -- tenant_id null, address, reason(HARD_BOUNCE|COMPLAINT|MANUAL), expires_at
mta.tls_reports             -- domain, period, policy, success_count, failure_count, details(jsonb)
mta.inbound_connections     -- (thống kê, giữ ngắn hạn) ip, ptr, helo, started_at, result

spam.rules                  -- code, description_vi, score, is_enabled, tenant_id null
spam.bayes_tokens           -- scope(GLOBAL|TENANT|USER), scope_id, token_hash, spam_count, ham_count
                            -- PK(scope, scope_id, token_hash)
spam.bayes_totals           -- scope, scope_id, spam_messages, ham_messages
spam.dnsbl_sources          -- name, zone, type(IP|DOMAIN), score, is_enabled, license_note

contact.contacts            -- user_id, display_name, emails(jsonb), phones(jsonb), org, title, notes,
                            -- photo_key, is_auto_saved, last_contacted_at, contact_count
contact.contact_groups / contact_group_members
contact.directory_overrides -- tenant_id, user_id, hide_from_directory

admin.policies              -- tenant_id, org_unit_id null, key, value(jsonb)
admin.routing_rules         -- tenant_id, direction(INBOUND|OUTBOUND), condition(jsonb), action(jsonb), order
admin.quarantine            -- tenant_id, blob_sha256, envelope(jsonb), reason, spam_report(jsonb),
                            -- released_by, released_at
admin.mail_log              -- tenant_id, direction, rfc_message_id, envelope_from, rcpt, size, status,
                            -- remote_ip, remote_host, smtp_response, spam_score, occurred_at
admin.dmarc_reports         -- domain_id, reporter, period_from, period_to, pass_count, fail_count, raw_key

compliance.retention_rules  -- tenant_id, scope(TENANT|ORG_UNIT|USER), scope_id, match(jsonb),
                            -- action(DELETE|KEEP), days
compliance.legal_holds      -- tenant_id, name, scope(USER|ORG_UNIT|QUERY), user_ids(jsonb), query(jsonb),
                            -- starts_at, ends_at, created_by, released_at
compliance.ediscovery_exports -- tenant_id, hold_id null, query(jsonb), status, object_key, item_count,
                            -- requested_by, finished_at

admin.migration_jobs        -- tenant_id, user_id, source(IMAP|MBOX|EML), host, port, username,
                            -- secret_encrypted, status, total, done, failed, errors(jsonb),
                            -- progress(jsonb: uidvalidity + last_uid theo thư mục)
admin.export_jobs           -- user_id, format(MBOX), status, object_key, expires_at

audit.audit_logs            -- tenant_id, user_id, actor_email, ip, user_agent, action, entity, entity_id,
                            -- old_value(jsonb), new_value(jsonb), result, occurred_at, prev_hash, hash
```

**Tìm kiếm**: `mail.messages.search_vector` dựng từ tiêu đề, người gửi/nhận, tên tệp đính kèm và **chữ thuần
rút từ thân thư** (cắt ở trần cấu hình, ví dụ 200 KB), qua hàm bất biến `f_unaccent` với cấu
hình tìm kiếm `simple` (không tách gốc từ theo ngôn ngữ nào) → tìm được mọi chữ Latin khi gõ không dấu. Chữ không
có khoảng trắng giữa các từ (CJK, Thái…) được tách thành bigram trước khi đưa vào vector, và câu tìm cũng tách y
như vậy. Chỉ mục GIN trên `search_vector`, trigram trên `subject` và `from_addr`. Thân thư không lưu ở
PostgreSQL ngoài vector và đoạn trích.

---

## 5. API (CHO WEB)

- Tiền tố `/api/v1`. Trả `{ "success": true, "data": {}, "message": "", "errors": [] }`; lỗi theo
  ProblemDetails có `code` ổn định và `title`/`detail` theo ngôn ngữ người gọi. Danh sách thư phân trang bằng **con trỏ** (khoá sắp xếp +
  id), không dùng số trang.
- **Đồng bộ tăng dần**: `GET /api/v1/mail/changes?sinceModseq=…` trả các thay đổi kể từ mốc — web dùng để làm
  mới sau khi mất kết nối SignalR, không tải lại cả hộp thư.
- Xác thực: Bearer token BlueID. Endpoint quản trị khai `[RequirePermission("mail.admin.<nhóm>.<hành động>")]`;
  endpoint người dùng chỉ thấy hộp thư của mình và hộp thư dùng chung được cấp — kiểm qua một cơ chế dùng chung
  trong `IMailStore` (mọi truy vấn nhận `MailboxAccess` đã xác thực), không tự viết ở từng handler. Phép thử
  quét: endpoint nào cũng phải có một trong ba: `[RequirePermission]`, `[AllowAnonymous]`, hoặc tên trong danh
  sách "tự canh" kèm lý do.
- Swagger đầy đủ, tiêu đề `BlueMail API`. Danh sách endpoint chi tiết trong `docs/05-api.md`.

---

## 6. ĐẶC TẢ CHỨC NĂNG NGƯỜI DÙNG (TƯƠNG ĐƯƠNG GMAIL)

### 6.1. Bố cục

- Thanh trên: logo khách hàng, ô tìm kiếm lớn (nút mở tìm kiếm nâng cao), trợ giúp, cài đặt, ảnh đại diện +
  menu tài khoản.
- Cột trái: nút **Soạn thư**; nhãn hệ thống (Hộp thư đến kèm số chưa đọc, Có gắn dấu sao, Đã tạm ẩn, Quan
  trọng, Đã gửi, Thư nháp kèm số lượng, Đã lên lịch, Tất cả thư, Thư rác, Thùng rác); "Thêm" thu gọn nhãn ít
  dùng; nhãn người dùng dạng cây có màu; hộp thư dùng chung được cấp; thanh dung lượng; thu gọn được cột.
- Giữa: danh sách hội thoại; khung đọc ở **bên phải**, **bên dưới** hoặc **không chia** theo cài đặt.
- Mật độ: Mặc định / Thoải mái / Gọn. Chủ đề sáng/tối/theo hệ thống. Tương phản đạt WCAG AA ở cả hai chế độ.
- Bố cục chạy tốt từ **1366×768**; ở **375 px** chuyển sang một cột, cài được như PWA.

### 6.2. Danh sách hội thoại

- Mỗi dòng: ô chọn, sao, dấu quan trọng, người gửi (gộp tên người trong hội thoại, đậm nếu chưa đọc, kèm số
  thư), nhãn, tiêu đề + đoạn trích, thẻ tên tệp đính kèm, thời gian (hôm nay: giờ; năm nay: ngày tháng; năm
  khác: ngày/tháng/năm — **theo múi giờ người dùng**, không bao giờ in giờ UTC).
- Rê chuột: lưu trữ, xoá, đánh dấu đã đọc/chưa đọc, tạm ẩn.
- **Chế độ hội thoại** bật/tắt.
- Chọn nhiều; **"Chọn tất cả N hội thoại khớp với tìm kiếm này"** — thao tác hàng loạt chạy nền khi vượt 500
  thư, có tiến độ, có trần (8.3).
- Thao tác hàng loạt: lưu trữ, xoá, đọc/chưa đọc, sao, gắn/bỏ nhãn, di chuyển, tạm ẩn, báo rác, tắt tiếng.
- Tab phân loại ở Hộp thư đến (bật/tắt từng tab): **Chính, Quảng cáo, Mạng xã hội, Cập nhật, Diễn đàn**.
  Kiểu hộp thư đến: Mặc định / Quan trọng trước / Chưa đọc trước / Có gắn sao trước.
- Kéo thả hội thoại sang nhãn ở cột trái.
- "1–50 trên khoảng N": con số là số thật hoặc ghi rõ "khoảng".
- Thư mới hiện **ngay** qua SignalR; tiêu đề tab trình duyệt hiện số chưa đọc; thông báo màn hình máy tính nếu
  người dùng cho phép.

### 6.3. Đọc thư

- Hội thoại hiện các thư theo thời gian; thư đã đọc thu gọn, thư chưa đọc và thư cuối mở sẵn; "N thư cũ hơn"
  gộp lại khi dài.
- **HTML thư đến**: lọc bằng HtmlSanitizer phía máy chủ **và** hiển thị trong `iframe sandbox` (không
  `allow-scripts`, không `allow-same-origin`) với CSP chặt qua `srcdoc`. Bỏ script, trình xử lý sự kiện,
  `javascript:`, form, `<meta refresh>`, CSS `expression`/`url()` ra ngoài. Ảnh `cid:` thay bằng địa chỉ nội
  bộ đã xác thực.
- **Ảnh từ xa bị chặn mặc định** với người gửi chưa tin cậy: "Hiển thị hình ảnh" / "Luôn hiển thị hình ảnh từ
  người gửi này". Khi hiển thị, ảnh đi qua **proxy ảnh** (ẩn IP và thời điểm mở thư; chặn SSRF: từ chối địa
  chỉ nội bộ, loopback, link-local, metadata đám mây, kiểm lại sau mỗi chuyển hướng; giới hạn cỡ và kiểu; đệm).
- Liên kết: `target="_blank" rel="noopener noreferrer"`; chữ hiển thị khác địa chỉ thật thì cảnh báo khi rê.
- **Biểu ngữ cảnh báo**: người gửi bên ngoài tổ chức (theo chính sách), SPF/DKIM/DMARC thất bại, tên miền giả
  dạng (khoảng cách chuỗi + ký tự đồng hình), người gửi lần đầu, tệp đính kèm nguy hiểm.
- Chi tiết người gửi: từ, tới, cc, ngày, tiêu đề, "gửi qua", "được ký bởi", mã hoá TLS khi nhận.
- Tệp đính kèm: thẻ xem trước; xem ảnh, PDF (pdf.js), văn bản thuần ngay trong trình duyệt; tệp Office chỉ tải
  về; **Tải về**, **Tải tất cả (ZIP)**. Tệp nhiễm virus không cho tải, hiện lý do.
- Thao tác: Trả lời, Trả lời tất cả, Chuyển tiếp, Chuyển tiếp dưới dạng tệp đính kèm, In, Xoá thư này, Chặn
  người gửi, Báo cáo thư rác, Báo cáo lừa đảo, **Hiển thị thư gốc** (MIME + bảng SPF/DKIM/DMARC), Tải thư
  (.eml), Đánh dấu chưa đọc từ đây, **"Vì sao thư này ở Thư rác?"** (bảng điểm 2.3).
- **Hủy đăng ký**: thư có `List-Unsubscribe` hiện nút cạnh người gửi; hỗ trợ một nhấp (RFC 8058) và `mailto:`.
- Lời mời lịch (`text/calendar`): thẻ sự kiện với Có / Có thể / Không, gửi phản hồi iMIP (RFC 6047). Lịch đầy
  đủ ngoài phạm vi.

### 6.4. Soạn thư

- Cửa sổ soạn nổi góc dưới phải; thu nhỏ, phóng to, bật ra cửa sổ riêng; nhiều cửa sổ soạn cùng lúc.
- Tới / Cc / Bcc: gợi ý từ danh bạ cá nhân, danh bạ tổ chức, nhóm, người hay liên lạc; dán danh sách tự tách;
  thẻ người nhận **ngoài tổ chức** đổi màu; kéo thả thẻ giữa ô; kiểm định dạng địa chỉ (kể cả địa chỉ quốc tế
  hoá theo RFC 6531).
- Chọn địa chỉ "Từ": chính chủ, bí danh, hộp thư dùng chung được cấp quyền gửi.
- Trình soạn TipTap: đậm, nghiêng, gạch chân, gạch ngang, phông, cỡ chữ, màu, căn lề, danh sách, thụt lề,
  trích dẫn, liên kết, **ảnh trong thân** (dán, kéo thả → phần `cid:`), bảng đơn giản, bỏ định dạng, văn bản
  thuần. Kiểm chính tả của trình duyệt.
- Thư dựng ra phải đúng chuẩn: `multipart/alternative` (thuần + HTML), `multipart/related` cho ảnh trong thân,
  `multipart/mixed` cho tệp; tên tệp ngoài ASCII mã hoá theo RFC 2231; tiêu đề ngoài ASCII theo RFC 2047;
  `Message-ID` sinh từ tên máy chủ cấu hình; `Date` có múi giờ.
- Tệp đính kèm: kéo thả, nhiều tệp, tiến độ, tải tiếp khi đứt mạng (tus). Tệp vượt ngưỡng (mặc định 25 MB) tự
  thành **liên kết tải** từ `bm-attachments-large`, hạn 30 ngày, đếm lượt tải. Chặn đuôi nguy hiểm theo chính
  sách. Quét bằng `BlueMail.Scanner`.
- **Tự lưu nháp** vài giây một lần và khi đóng; nháp nằm trong nhãn Thư nháp (về sau IMAP thấy được).
- Chữ ký nhiều mẫu, mặc định theo thư mới/trả lời/địa chỉ gửi; vị trí trên/dưới phần trích dẫn.
- Mẫu thư: lưu nháp làm mẫu, chèn mẫu.
- Trả lời giữ đúng `In-Reply-To`, `References`; trích dẫn thu gọn; tự thêm "Re:"/"Fwd:".
- **Gửi** (Ctrl+Enter), **Gửi và lưu trữ**, **Hẹn giờ gửi** (gợi ý sáng mai 8:00, chiều nay 13:00, thứ Hai
  tới 8:00, hoặc chọn) — thư hẹn nằm ở "Đã lên lịch", huỷ/sửa được.
- **Hoàn tác gửi** 5/10/20/30 giây: "Đã gửi thư. Hoàn tác · Xem thư"; hoàn tác thì mở lại cửa sổ soạn y như
  trước. Thư chỉ được nộp vào hàng đợi khi hết cửa sổ. **Một nháp không bao giờ bị gửi hai lần** (ràng buộc
  duy nhất ở `mail.outgoing` + chuyển trạng thái nguyên tử).
- Cảnh báo trước khi gửi: tiêu đề trống; thân nhắc "đính kèm"/"tệp đính kèm"/"attached" mà không có tệp; gửi ra
  ngoài số lượng lớn; trả lời tất cả với danh sách dài.
- **Chế độ bảo mật** (Phase 12): thư ra ngoài dưới dạng liên kết tới trang xem của BlueMail, có hạn, tuỳ chọn
  OTP qua thư, thu hồi được. Ghi rõ không ngăn được chụp màn hình.

### 6.5. Nhãn và sắp xếp

- Nhãn lồng nhau (`Dự án/BlueMail`), màu nền + màu chữ từ bảng màu cố định đạt tương phản, ẩn/hiện ở cột
  trái và trong danh sách thư.
- **Lưu trữ** = bỏ nhãn Hộp thư đến. **Xoá** = vào Thùng rác, xoá hẳn sau 30 ngày (trừ thư bị giữ pháp lý).
  Thư rác tự xoá sau 30 ngày.
- Gắn sao; **Quan trọng** đánh tay hoặc theo quy tắc giải thích được (người bạn hay trả lời, thư gửi thẳng
  cho bạn, người cùng tổ chức).
- **Tạm ẩn**: hội thoại rời Hộp thư đến sang "Đã tạm ẩn"; tới giờ quay lại đầu Hộp thư đến, chưa đọc. Thư mới
  trong hội thoại đang tạm ẩn làm nó quay lại ngay.
- **Tắt tiếng hội thoại**: thư mới tự lưu trữ, trừ khi gửi riêng cho bạn.

### 6.6. Phân loại Hộp thư đến

- Lúc giao thư, phân loại **Chính / Quảng cáo / Mạng xã hội / Cập nhật / Diễn đàn** bằng quy tắc giải thích
  được: `List-Id`, `List-Unsubscribe`, `Precedence: bulk`, `Auto-Submitted`, danh sách tên miền
  (`mail.category_rules`, quản trị viên sửa được); người gửi có trong danh bạ luôn vào Chính.
- Kéo thư sang tab khác → hỏi "Làm vậy với thư sau này từ người gửi này?" → tạo quy tắc cá nhân.

### 6.7. Tìm kiếm

- Gợi ý khi gõ (người liên hệ, nhãn, tìm gần đây). Tìm được khi gõ không dấu và tìm được chuỗi con trong chữ CJK (mục 0.2).
- **Toán tử như Gmail** — bộ phân tích cú pháp tự viết, phép thử đơn vị cho từng toán tử và cho câu sai (câu sai
  báo lỗi rõ, **không** âm thầm coi cả câu là từ khoá); dịch thành SQL tham số hoá, không ghép chuỗi:

| Toán tử | Ví dụ |
|---|---|
| `from:` `to:` `cc:` `bcc:` | `from:an@acme.vn`, `from:me` |
| `subject:` | `subject:"hợp đồng"` |
| `"cụm từ"`, `OR`, `{a b}`, `-`, `( )` | `hop dong OR bao gia -nhap` |
| `label:` `in:` | `in:inbox`, `in:sent`, `in:spam`, `in:trash`, `in:anywhere`, `label:du-an` |
| `is:` | `is:unread`, `is:read`, `is:starred`, `is:important`, `is:snoozed`, `is:muted` |
| `has:` | `has:attachment`, `has:userlabels`, `has:nouserlabels` |
| `filename:` | `filename:pdf`, `filename:baocao.xlsx` |
| `category:` | `category:promotions` |
| `after:` `before:` `older:` `newer:` | `after:2026/01/01` (theo múi giờ người dùng) |
| `older_than:` `newer_than:` | `newer_than:7d`, `older_than:1y` |
| `larger:` `smaller:` `size:` | `larger:10M` |
| `list:` `deliveredto:` `rfc822msgid:` | |

- **Tìm kiếm nâng cao**: biểu mẫu → sinh chuỗi toán tử; nút **"Tạo bộ lọc"** từ tiêu chí ấy.
- Khoảng ngày ngược báo lỗi rõ.

### 6.8. Bộ lọc, chặn, trả lời tự động, chuyển tiếp

- Mọi thứ ở mục này chạy **trong đường giao thư phía máy chủ** (2.1 bước 8) — có tác dụng dù người dùng
  đọc bằng web hay bằng Outlook.
- **Bộ lọc**: tiêu chí như tìm kiếm nâng cao; hành động: bỏ qua Hộp thư đến, đánh dấu đã đọc, gắn sao, gắn
  nhãn, chuyển tiếp (chỉ tới địa chỉ đã xác minh), xoá, không bao giờ vào thư rác, luôn/không bao giờ quan
  trọng, phân loại tab. "Áp dụng cho N hội thoại đã có" chạy nền. **Nhập/xuất XML bộ lọc của Gmail.** Bộ
  so khớp của bộ lọc và bộ tìm kiếm dùng **chung một** cây biểu thức đã phân tích — hai lối cùng một luật.
- **Chặn người gửi** → thư sau vào Thư rác.
- **Trả lời tự động**: ngày bắt đầu/kết thúc, tiêu đề, nội dung, chỉ cho danh bạ / trong tổ chức; tuân thủ
  **RFC 3834**: không trả lời thư `Auto-Submitted`, thư danh sách, `Precedence: bulk`, người gửi rỗng; mỗi
  người gửi một lần trong N ngày (`mail.vacation_replies`). Đang bật thì có thanh nhắc trên đầu trang.
- **Chuyển tiếp tự động**: sau khi **xác minh bằng mã gửi tới địa chỉ đích**; giữ/không giữ bản sao; chuyển
  tiếp dùng **SRS** (Sender Rewriting Scheme) để không làm hỏng SPF; chặn vòng lặp (đếm tiêu đề `Received`,
  đánh dấu tiêu đề riêng). Quản trị viên có thể cấm chuyển tiếp ra ngoài.

### 6.9. Thư rác và lừa đảo

- "Báo cáo thư rác" / "Không phải thư rác" → di chuyển thư + học Bayes cho người dùng (và có trọng số cho
  tenant).
- "Báo cáo lừa đảo" → vào Thư rác + gửi bản sao cho hộp thư bảo mật của tổ chức (cấu hình được).
- Thư nhiễm virus không bao giờ tới hộp thư; nếu đã nhận trong phiên thì vào cách ly của quản trị viên.

### 6.10. Danh bạ

- Danh bạ cá nhân: tên, nhiều thư, nhiều số điện thoại, tổ chức, chức danh, ghi chú, ảnh; nhóm liên hệ.
- **Tự lưu** người bạn gửi tới vào "Liên hệ khác".
- **Danh bạ tổ chức**: người dùng, nhóm, hộp thư dùng chung (quản trị viên ẩn được người cụ thể).
- Nhập/xuất **vCard** (RFC 6350) và **CSV** (khuôn Google Contacts, Outlook). Gộp liên hệ trùng.
- CardDAV cho điện thoại: ngoài phạm vi bản này.

### 6.11. Cài đặt

Tab như Gmail: **Chung** (ngôn ngữ, múi giờ, số hội thoại/trang, hoàn tác gửi, trả lời mặc định, kiểu văn bản,
chế độ hội thoại, ảnh từ xa, phím tắt, thông báo, chữ ký, trả lời tự động, gửi và lưu trữ, tự chuyển thư kế),
**Nhãn**, **Hộp thư đến**, **Tài khoản** (địa chỉ gửi thay, đổi mật khẩu, mật khẩu ứng dụng, dung lượng),
**Bộ lọc và địa chỉ bị chặn**, **Chuyển tiếp và IMAP** (bật/tắt IMAP — Phase 11, hướng dẫn cấu hình
Outlook/Thunderbird/iPhone có sẵn máy chủ và cổng), **Mẫu thư**, **Giao diện**.

### 6.12. Phím tắt (bật trong cài đặt; `?` mở bảng)

`c` soạn · `/` tìm kiếm · `j`/`k` hội thoại kế/trước · `n`/`p` thư kế/trước · `o`/`Enter` mở · `u` về danh
sách · `e` lưu trữ · `#` xoá · `!` báo rác · `s` sao · `+`/`-` quan trọng · `x` chọn · `r` trả lời · `a` trả lời
tất cả · `f` chuyển tiếp · `Shift+i`/`Shift+u` đọc/chưa đọc · `l` nhãn · `v` di chuyển · `b` tạm ẩn · `m` tắt
tiếng · `z` hoàn tác · `g i`/`g s`/`g t`/`g d`/`g a` tới Hộp thư đến/Có sao/Đã gửi/Nháp/Tất cả · `* a`/`* n`
chọn/bỏ chọn tất cả · `Ctrl+Enter` gửi · `Esc` đóng. Mọi chức năng dùng được bằng bàn phím; phần tử bấm được
tới được bằng Tab và có nhãn cho trình đọc màn hình.

### 6.13. Ngoài phạm vi

Tích hợp BlueDrive/BlueOffice, lịch đầy đủ, CalDAV/CardDAV, POP3, JMAP, ứng dụng di động gốc. **Không viết
interface, cấu hình, nút hay bảng chờ sẵn cho các thứ này.** Thiết kế đã để ngỏ đường: BlueID là OIDC chuẩn,
kho thư có modseq/UID.

---

## 7. BẢNG ĐIỀU KHIỂN QUẢN TRỊ

Hai cấp: **quản trị nền tảng** (chủ dịch vụ, mọi tenant) và **quản trị viên tenant**. Quyền chi tiết
`mail.admin.<nhóm>.<hành động>`; 403 có mã lỗi rõ ràng khi thiếu quyền; giao diện ẩn chức năng không có quyền nhưng
**máy chủ vẫn tự kiểm**.

### 7.1. Tenant (quản trị nền tảng)

Tạo/sửa/tạm khoá tenant (khoá thì ngừng nhận thư với `450`/`550` theo cấu hình, ngừng gửi, đăng nhập bị
chặn), gói dịch vụ, gán dải IP gửi, thương hiệu, thống kê dùng, đăng nhập hộ để hỗ trợ (ghi nhật ký, báo
quản trị viên tenant).

### 7.2. Tên miền — trình hướng dẫn

1. Nhập tên miền → sinh bản ghi TXT xác minh quyền sở hữu.
2. Hiện **đúng các bản ghi cần thêm**: TXT xác minh, MX, SPF, DKIM (sinh khoá ngay), DMARC (gợi ý `p=none`
   kèm `rua` về hộp thư báo cáo của BlueMail), MTA-STS (BlueMail phục vụ tệp chính sách tại
   `mta-sts.<tên miền>`) + TLS-RPT, `autoconfig`/`autodiscover` và SRV (RFC 6186); nút sao chép từng bản ghi.
3. **Kiểm tra** tra DNS thật, hiện đúng/sai từng bản ghi và giá trị đang có; việc nền kiểm lại định kỳ và báo
   quản trị viên khi hỏng. Chỉ nhận thư cho tên miền đã xác minh.
4. Tên miền phụ, bí danh tên miền, catch-all.
5. **Xoay khoá DKIM** có lịch: sinh khoá mới (PENDING) → chờ DNS có bản ghi mới → kích hoạt → giữ khoá cũ để
   kiểm thư đang trên đường → nghỉ.

### 7.3. Người dùng, nhóm, bí danh, hộp thư dùng chung

- Người dùng: thêm (đơn lẻ và **nhập CSV** — kiểm trùng, sai định dạng, bảng lỗi sửa được tại chỗ), sửa, đặt lại
  mật khẩu (buộc đổi), bắt buộc 2FA, tạm khoá (**phiên web, phiên IMAP đang mở và mật khẩu ứng dụng mất hiệu
  lực ngay**), xoá (xoá mềm; tuỳ chọn chuyển thư cho người khác trước), đổi địa chỉ chính (giữ cũ làm bí danh),
  hạn mức, đơn vị tổ chức, đăng nhập gần đây theo giao thức.
- Nhóm/danh sách phân phối: thành viên trong và ngoài, ai được gửi vào, kiểm duyệt, cho phép người ngoài. Thư
  vào nhóm được mở rộng ở bước giao thư; thư ra thành viên bên ngoài đi qua hàng đợi với SRS.
- Bí danh, hộp thư dùng chung (đọc, gửi với tư cách, gửi thay).

### 7.4. Chính sách và định tuyến

Theo tenant hoặc đơn vị: cỡ thư tối đa, đuôi tệp bị chặn, cấm chuyển tiếp ra ngoài, bắt buộc 2FA, chính sách
mật khẩu, khoá sau N lần sai, biểu ngữ người gửi bên ngoài, danh sách cho phép/chặn cấp tổ chức, ngưỡng rác,
greylisting, chân thư bắt buộc cho thư ra ngoài, quy tắc định tuyến (thêm người nhận ẩn để lưu trữ, chuyển
hướng, từ chối theo điều kiện), cách ly theo điều kiện. **Mỗi khoá chính sách phải có chỗ đọc trong mã**;
phép thử quét bảo đảm không có khoá nào không ai đọc.

### 7.5. Tuân thủ

- **Lưu giữ**: tự xoá thư cũ hơn N ngày theo điều kiện, hoặc giữ tối thiểu N ngày.
- **Giữ pháp lý**: theo người, đơn vị hoặc truy vấn; thư bị giữ **không xoá hẳn được** kể cả khi người dùng dọn
  Thùng rác (biến khỏi giao diện người dùng, còn cho quản trị viên tuân thủ). Việc dọn Thùng rác/lưu giữ phải
  kiểm giữ pháp lý **trong cùng câu truy vấn xoá**.
- **eDiscovery**: tìm trên nhiều hộp thư bằng cùng cú pháp 6.7, xem trước, xuất mbox/eml kèm tệp kê khai có
  SHA-256; mọi lượt xuất ghi nhật ký.
- **Nhật ký kiểm toán** append-only có chuỗi băm.

### 7.6. Vận hành thư

- **Tìm vết thư**: theo người gửi/nhận/Message-ID/thời gian; thấy thư đã nhận từ IP nào, kết quả xác thực,
  điểm rác, giao vào đâu; thư gửi đi qua MX nào, mã phản hồi, bị trả lại vì sao.
- **Hàng đợi gửi đi**: đang chờ, đang hoãn (lý do lỗi cuối), thử lại ngay, huỷ; theo tên miền đích.
- **Cách ly**: xem tiêu đề an toàn, xem bảng điểm, thả/xoá.
- **Danh sách chặn gửi**: xem, gỡ.
- **Sức khoẻ gửi thư**: tỉ lệ giao thành công theo tên miền đích, tỉ lệ trả lại, khiếu nại, IP có nằm trong
  danh sách đen công khai không, tiến độ làm ấm IP.
- **Báo cáo**: thư nhận/gửi theo ngày, tỉ lệ rác, dung lượng theo người dùng, người dùng chưa bật 2FA, tổng hợp
  báo cáo DMARC và TLS-RPT nhận về. Mỗi báo cáo có bảng, biểu đồ, xuất Excel/PDF; **tệp xuất ghi rõ bộ lọc đã
  áp và có bị cắt do chạm trần không**.

### 7.7. Chuyển dữ liệu

- **Nhập thư** từ IMAP bất kỳ (Gmail, Outlook.com, Microsoft 365, Yahoo, máy chủ riêng) bằng máy khách IMAP tự viết (2.6.5): theo
  người hoặc hàng loạt từ CSV; ánh xạ thư mục → nhãn (`[Gmail]/Sent Mail` → Đã gửi, nhãn Gmail giữ đủ qua phần
  mở rộng `X-GM-LABELS` khi nguồn là Gmail); giữ ngày, cờ; **chạy lại không nhập trùng** (UIDVALIDITY + UID +
  băm nội dung); tiến độ, nhật ký lỗi tải về. Thư nhập **không** chạy bộ lọc, trả lời tự động, chuyển tiếp.
- **Nhập mbox / .eml** tải lên.
- **Xuất dữ liệu người dùng**: mbox theo nhãn + vCard + JSON cài đặt, ZIP, liên kết có hạn.
- **Xuất toàn bộ tenant** khi chấm dứt hợp đồng.

---

## 8. YÊU CẦU PHI CHỨC NĂNG

### 8.1. Bảo mật

- HTTPS, HSTS, CSP chặt (không `unsafe-inline` cho script), X-Frame-Options, X-Content-Type-Options,
  Referrer-Policy.
- TLS cho SMTP/IMAP: chứng chỉ tự động (dùng chung nguồn với Caddy hoặc ACME riêng), chỉ TLS 1.2+, bộ mã an
  toàn; tải lại chứng chỉ không cần khởi động lại tiến trình.
- Mật khẩu Argon2id; **2FA TOTP** kèm mã khôi phục; danh sách phiên; cảnh báo đăng nhập mới; **mật khẩu ứng
  dụng** cho IMAP/SMTP (bắt buộc khi đã bật 2FA); `XOAUTH2`/`OAUTHBEARER` cho phần mềm hỗ trợ.
- Chặn dò mật khẩu qua IMAP/SMTP AUTH: đếm theo tài khoản và theo IP, khoá tạm, ghi `login_events`.
- Mọi câu hỏi "chủ phiên này còn được vào không" (tài khoản/tenant bị khoá, phiên bị thu hồi, đổi mật khẩu) ở
  **một chỗ** (`ISessionValidator` theo `security_stamp`) dùng cho cả web, IMAP, SMTP nộp thư; phiên IMAP đang
  mở bị ngắt khi tài khoản bị khoá.
- Giới hạn tốc độ: đăng nhập, quên mật khẩu, gửi thư, proxy ảnh, tìm kiếm; trả **429 JSON đúng khuôn lỗi của API** ở cả
  proxy phía trước.
- IP người dùng chỉ lấy từ proxy tin cậy; với SMTP/IMAP là IP của kết nối TCP (hỗ trợ PROXY protocol nếu đặt
  sau bộ cân bằng tải, chỉ từ dải tin cậy).
- Tệp tải lên: kiểm **chữ ký byte**, giới hạn cỡ, `BlueMail.Scanner`, lưu ngoài web root.
- Lọc ký tự PostgreSQL không lưu được (U+0000) ở **cửa vào** — API, và cả dữ liệu rút từ thư (tiêu đề, tên tệp).
- Mã hoá khi lưu: nội dung thư (khoá tenant), khoá DKIM riêng, bí mật TOTP, mật khẩu IMAP nguồn.
- Không ghi nhật ký nội dung thư, mật khẩu, thẻ, OTP, lệnh `AUTH`.
- **Cách ly tenant**: bộ lọc toàn cục EF Core + mọi truy vấn của `IMailStore` nhận `MailboxAccess`; phép thử
  truy cập chéo trên mọi endpoint và trên IMAP (`SELECT` hộp thư người khác).
- **Fuzzing** bộ phân tích SMTP, IMAP, MIME, DNS (gói tin trả về), DKIM, SPF, toán tử tìm kiếm, và bộ đọc tệp nén/Office/PDF của `BlueMail.Scanner` — chạy trong CI
  một khoảng ngắn, chạy dài định kỳ.

### 8.2. Hiệu năng (ngưỡng chốt ở Z5)

- Mở Hộp thư đến (50 hội thoại) < 400 ms p95 với hộp thư 100.000 thư.
- Tìm kiếm < 1 giây p95 với hộp thư 1.000.000 thư.
- Nhận thư: tối thiểu 50 thư/giây duy trì trên một máy 8 nhân, `250` trả trong < 1 giây p95 cho thư < 1 MB.
- 500 người dùng web đồng thời; 2.000 kết nối IMAP IDLE đồng thời (Phase 11).
- Không đọc nguyên thư lớn vào RAM ở bất kỳ đâu (nhận, gửi, xem, xuất) — dùng luồng.

### 8.3. Việc dài và hàng loạt

- Việc dài (thao tác > 500 thư, áp bộ lọc cho thư cũ, nhập/xuất, eDiscovery) không chạy trong lượt HTTP —
  Hangfire, khoá chống chạy trùng, tiến độ, đóng việc chết.
- **Mọi lệnh nhận mảng đều có trần** (ví dụ 1.000 mã thư/yêu cầu); vượt trần báo lỗi rõ.
- Thư tổng hợp theo ngày chỉ gửi một lần mỗi ngày dù việc chạy lại.

### 8.4. Vận hành 24/7

- `/health` và `/health/ready` cho mỗi tiến trình (PostgreSQL, S3, Valkey; Mx/Imap báo thêm cổng đang
  nghe).
- **Đóng máy êm**: Mx ngừng nhận kết nối mới, trả `421` cho phiên đang chờ, đợi giao dịch đang dở xong; Outbound
  trả việc về hàng đợi; không mất thư, không gửi hai lần.
- **Không mất thư khi sập giữa chừng**: phép thử giết tiến trình giữa lúc giao thư và giữa lúc gửi, chứng minh
  thư hoặc đã giao đủ, hoặc bên gửi nhận được lỗi tạm và gửi lại — không có trạng thái nửa vời.
- Việc nền định kỳ: trả thư tạm ẩn, nộp thư hẹn giờ, dọn Thùng rác/Thư rác 30 ngày (trừ giữ pháp lý), kiểm DNS
  tên miền, xoay khoá DKIM, đọc báo cáo DMARC/TLS-RPT, áp lưu giữ, dọn tệp lớn hết hạn, cập nhật Public Suffix
  List, dọn đối tượng S3 không còn thư nào trỏ tới (**chỉ sau thời gian chờ an toàn và kiểm trong cùng truy vấn**).
  Đổi lịch trong cài đặt thì đăng ký lại ngay.
- Sao lưu PostgreSQL + S3, giữ N bản, phục hồi đã thử thật (`docs/03` ghi số đo).
- Hangfire Dashboard chỉ cho quản trị nền tảng.

---

## 9. DOCKER

- `docker-compose.yml`: `postgres`, `valkey`, `s3`, `identity`, `api`, `worker`, `mx`, `submission`,
  `outbound`, `imap` (từ Phase 11), `web`, `caddy`. Cổng ra ngoài: 80/443 (Caddy), 25 (mx), 465/587
  (submission), 993 (imap). Giới hạn tài nguyên, `restart: unless-stopped`, log xoay vòng. `outbound` cần cấu
  hình IP nguồn (chạy `network_mode` phù hợp hoặc gán IP) — ghi rõ trong `docs/04`.
- `docker-compose.dev.yml`: + CoreDNS và Postfix/OpenDKIM `doitac.test` (chỉ để kiểm thử).
- Dockerfile nhiều tầng; một image .NET dùng chung, chọn tiến trình bằng lệnh khởi động, hoặc image riêng mỗi
  tiến trình — chọn một, ghi `docs/00`.
- Cấu hình qua biến môi trường, `.env.example` có chú thích; thiếu biến bắt buộc thì dừng với thông báo
  nói rõ biến nào thiếu.
- Ghim phiên bản mọi image. Kịch bản triển khai tự dọn image cũ, giữ bản đang chạy và bản trước; thân kịch bản
  bọc trong `main()`.
- `docker compose up -d` là có hệ thống đầy đủ với dữ liệu mẫu.

## 10. DỮ LIỆU MẪU

- Tài khoản quản trị nền tảng (mật khẩu từ `.env`, buộc đổi lần đầu).
- Hai tenant `bluemail.test`, `acme.test`, mỗi tenant 10–20 người dùng **tên không trùng, từ nhiều nước** (một tenant mặc định `en`, một tenant `vi`), nhóm, bí danh,
  hộp thư dùng chung, đơn vị tổ chức.
- Hộp thư mẫu được dựng **bằng cách gửi thư thật qua SMTP** (từ chính BlueMail và từ Postfix `doitac.test`),
  không chèn thẳng CSDL: hội thoại nhiều lượt, tệp đính kèm tên ngoài ASCII, thư bằng nhiều ngôn ngữ và hệ chữ (kể cả một thư phải-sang-trái), HTML có ảnh từ xa, bản tin có
  `List-Unsubscribe`, thư rác mẫu, thư DMARC thất bại, lời mời lịch, nhãn lồng nhau, tạm ẩn, nháp, hẹn giờ.
- Mốc thời gian tính từ ngày nạp; không thư nào ở tương lai (trừ thư hẹn giờ).
- Rào chống nạp lại của mỗi phần dữ liệu mẫu hỏi về **chính phần ấy**.

## 11. KIỂM THỬ

- **Mỗi lỗi sửa kèm phép thử đỏ trước, xanh sau.** Phép thử xanh ngay từ đầu là chưa dựng đúng bối cảnh.
- **Unit**: máy trạng thái SMTP (mọi lệnh, sai thứ tự, dòng quá dài, pipelining), SPF (bộ phép thử chính thức
  của RFC 7208 — **tệp YAML của OpenSPF**, nạp nguyên văn), DMARC, tên miền tổ chức qua PSL, ghép hội thoại, bộ
  phân tích toán tử tìm kiếm, bộ lọc, RFC 3834, SRS, lọc HTML (bộ mẫu XSS chuẩn), chặn SSRF của proxy ảnh, lịch
  thử lại, tính múi giờ (kể cả ngày đổi giờ mùa hè), tách từ đa ngôn ngữ, IDNA, tiền tố tiêu đề nhiều ngôn ngữ.
- **Integration** (Testcontainers PostgreSQL + S3): mọi endpoint; giao thư → thấy trên web qua SignalR; hoàn tác
  gửi; hẹn giờ gửi qua khởi động lại; **ba yêu cầu thật sự song song gửi cùng một nháp chỉ ra một thư**; cấp UID
  song song không trùng; khoá tài khoản ngắt phiên ngay; giữ pháp lý chặn dọn Thùng rác.
- **Interop** (mạng dev với Postfix `doitac.test` + CoreDNS) — **kiểm bằng phần mềm của người khác**:
  - Postfix gửi cho BlueMail: nhận đúng, `Authentication-Results` đúng; Postfix nhận thư BlueMail gửi: OpenDKIM
    của Postfix ghi `dkim=pass`; `dkimpy`/`authheaders` xác nhận DKIM/ARC; `checkdmarc` đọc bản ghi đúng.
  - `swaks` bắn các trường hợp: relay mở (phải bị từ chối), người nhận không tồn tại (`550` ngay ở RCPT), thư
    quá cỡ, `BDAT`, `SMTPUTF8`, `STARTTLS` rồi gửi lệnh trước khi bắt tay xong (STARTTLS injection — phải bỏ đệm).
  - Postfix tạm trả `451` → BlueMail thử lại đúng lịch; trả `550` → DSN về người gửi, đọc được bằng `email` của
    Python.
  - mbox xuất ra mở được bằng `mailbox` của Python; vCard bằng thư viện vCard độc lập.
  - (Phase 11) **`imaptest`** của Dovecot chạy bộ kiểm tuân thủ IMAP; Thunderbird qua kịch bản tự động nếu
    làm được, nếu không thì kịch bản tay ghi trong `docs/06`.
- **Đối chứng cho thư viện tự viết** (chạy trong CI, công cụ chỉ có trong container kiểm thử):
  - `BlueMail.Mime` ↔ `email` của Python trên **toàn bộ** `tests/BlueMail.Corpus`: so tiêu đề đã giải mã, cây
    phần, tên tệp, `Content-Type`, nội dung đã giải mã (băm). Lệch thì phép thử đỏ và in ra thư gây lệch; chỗ nào
    chủ ý khác Python thì ghi vào danh sách ngoại lệ kèm lý do.
  - `BlueMail.Dns` ↔ `dnspython` trên CoreDNS (đủ loại bản ghi, NXDOMAIN, bị cắt phải sang TCP, CNAME lồng).
  - `BlueMail.Dkim` ↔ `dkimpy`/`authheaders`: thư mình ký thì họ kiểm `pass`; thư họ ký thì mình kiểm `pass`;
    sửa một byte thì cả hai bên cùng `fail`.
  - `BlueMail.Scanner` ↔ ClamAV trên bộ mẫu: liệt kê tệp hai bên kết luận khác nhau để xem xét (không bắt buộc
    trùng, vì hai bên chặn theo cách khác nhau).
- **Fuzz**: SharpFuzz cho bộ phân tích SMTP/IMAP/MIME/DNS/DKIM/SPF/toán tử và bộ đọc tệp của `BlueMail.Scanner`;
  không ngoại lệ chưa bắt, không treo, không vượt bộ nhớ.
- **Sập giữa chừng**: giết tiến trình Mx/Outbound ở các điểm cấu hình được, chứng minh không mất và không trùng thư.
- **Phân trang**: đi hết các trang gặp đúng `total` dòng khác nhau; mọi sắp xếp kết thúc bằng khoá duy nhất.
- **Bộ lọc danh sách**: giá trị không thể khớp phải trả 0.
- **E2E Playwright**: đăng nhập, đọc, soạn có tệp, hoàn tác gửi, tìm có toán tử, tạo bộ lọc từ tìm kiếm, tạm ẩn,
  quản trị thêm tên miền và người dùng; chạy ở **1366×768 và 375×812**, kiểm không trang nào cuộn ngang.
- **Truy cập**: phần tử có `onClick` tới được bằng bàn phím; tương phản đo bằng máy ở cả hai chế độ.
- **Chất lượng lọc rác**: báo cáo tỉ lệ bắt đúng/bắt nhầm trên tập mẫu sau mỗi lần đổi luật; bắt nhầm vượt
  ngưỡng thì CI đỏ.
- **Tải**: k6 cho web; công cụ bắn SMTP cho Mx (đạt ngưỡng 8.2).
- `docs/06-kich-ban-kiem-thu.md`: Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt.

## 12. QUY ƯỚC CODE

- API: mỗi tính năng một thư mục `Application/Features/<Tên>/` (Command/Query + Handler + Validator + DTO). Không
  logic nghiệp vụ trong Controller. Ngoại lệ xử lý tập trung; bộ ghi nhật ký yêu cầu đứng **trước** bộ xử lý
  ngoại lệ.
- Giao thức: bộ phân tích lệnh tách khỏi máy trạng thái, máy trạng thái tách khỏi I/O — để unit test và fuzz
  không cần mạng. Mỗi lệnh ghi RFC và mục tương ứng trong chú thích.
- Mọi ghi vào hộp thư qua `IMailStore`; phép thử quét cấm `DbSet<Message>`/`DbSet<MessageLabel>` được dùng để
  ghi ngoài `BlueMail.MailStore`.
- **Mọi tiến trình chạy `InvariantCulture`**; định dạng cho người dùng lấy văn hoá từ yêu cầu (ngôn ngữ người
  dùng), không từ máy chủ. Mọi thứ đi ra mạng dùng định dạng chuẩn (ngày trong tiêu đề thư theo RFC 5322, số trong phản hồi SMTP/IMAP). Phép thử quét định dạng
  ngày giờ ở cả hai phía.
- Mốc giờ hiện cho người dùng đổi sang múi giờ người dùng.
- Frontend: `web/src/modules/` (`mail`, `compose`, `search`, `settings`, `contacts`, `admin`); không `any`; màu chỉ
  từ token (cấm mã màu viết thẳng trong TSX); chuỗi giao diện ở `locales/en.json`, `locales/vi.json`; bố cục dùng thuộc tính logic CSS để lật được RTL; gọi API chỉ qua `src/api`.
- Migration tên có nghĩa, không sửa migration đã commit; sửa lỗi nghiệp vụ kèm migration dọn dữ liệu cũ; phép thử
  bảo đảm mọi lớp migration có `[Migration]`.
- Sau mỗi phase: build sạch không cảnh báo, mọi phép thử xanh, cập nhật `README.md`, `CLAUDE.md`, `docs/`, commit
  `phase-N: …`.

---

## 13. THỨ TỰ THỰC HIỆN (TUẦN TỰ, KHÔNG NHẢY BƯỚC)

| Phase | Nội dung | Nghiệm thu |
|---|---|---|
| **0** | Nền kiểm thử và quyết định Z1–Z8 | Postfix `doitac.test` gửi/nhận được trong mạng dev; công cụ đối chứng chạy được; bộ thư mẫu đã có |
| **1** | Nền móng: solution, Docker, PostgreSQL, BlueID (đăng nhập, 2FA, phiên), tenant + bộ lọc toàn cục, nhật ký chuỗi băm, health, Serilog, khung React; **lược đồ kho thư đầy đủ** (có modseq/UID) | Đăng nhập web; truy cập chéo tenant bị chặn |
| **2** | **Thư viện nền tự viết**: `BlueMail.Mime` (phân tích + dựng), `BlueMail.Dns` (không DNSSEC), `BlueMail.Dkim` (DKIM + ARC), `BlueMail.Scanner` lớp 1–6 | Mọi thư trong bộ thư mẫu phân tích không ném lỗi, kết quả khớp `email` của Python ở tiêu đề, cây phần, tên tệp, nội dung giải mã; thư dựng ra được `email` của Python và Postfix đọc đúng; `BlueMail.Dns` trả cùng kết quả với `dnspython` trên CoreDNS; chữ ký DKIM của mình được `dkimpy` kiểm `pass` và ngược lại; bộ quét chặn đủ bộ mẫu nguy hiểm, không chặn nhầm bộ mẫu sạch; fuzz chạy sạch |
| **3** | `IMailStore` + kho S3 mã hoá + ghép hội thoại + tìm kiếm đa ngôn ngữ; **BlueMail.Mx**: SMTP nhận vào, kiểm người nhận, SPF/DKIM/DMARC/ARC, quét tệp, giao thư | Postfix gửi vào → thư nằm đúng hộp thư, `Authentication-Results` đúng, `swaks` không làm relay mở được |
| **4** | Web đọc thư: nhãn, danh sách hội thoại, khung đọc, HTML an toàn, proxy ảnh, tệp đính kèm, thao tác đơn lẻ/hàng loạt, thời gian thực, đồng bộ tăng dần | Dùng được như hộp thư chỉ-nhận |
| **5** | **Gửi**: soạn, nháp, tệp, chữ ký, mẫu, trả lời/chuyển tiếp, hoàn tác, hẹn giờ; **BlueMail.Outbound**: ký DKIM, hàng đợi, MX, TLS/MTA-STS, thử lại, DSN, chặn gửi; giao nội bộ | Postfix nhận thư với `dkim=pass`; lỗi tạm thử lại đúng lịch; không gửi trùng khi giết tiến trình |
| **6** | **Lọc rác và quét tệp đầy đủ**: bộ luật, Bayes, danh tiếng, DNSBL (theo Z4), từ chối trong phiên, cách ly, "Vì sao ở Thư rác?", học từ nút báo cáo; `BlueMail.Scanner` lớp 7–8 (danh sách băm theo Z7, bộ luật mẫu byte) | Báo cáo bắt đúng/bắt nhầm của cả lọc rác và quét tệp đạt ngưỡng |
| **7** | Tìm kiếm toán tử + nâng cao; bộ lọc (dùng chung cây biểu thức), chặn, trả lời tự động RFC 3834, chuyển tiếp có xác minh + SRS + ký ARC, nhập/xuất bộ lọc Gmail | Bộ lọc chạy đúng với thư đến qua SMTP |
| **8** | Sắp xếp: tab phân loại, quan trọng, tạm ẩn, tắt tiếng, hủy đăng ký, báo lừa đảo; danh bạ, danh bạ tổ chức, vCard/CSV; cài đặt đầy đủ; phím tắt | |
| **9** | Quản trị: tenant, trình hướng dẫn tên miền + kiểm DNS + MTA-STS, xoay DKIM, người dùng (CSV), nhóm, bí danh, hộp thư dùng chung, chính sách, định tuyến, tìm vết thư, hàng đợi, cách ly, chặn gửi, sức khoẻ gửi thư, báo cáo | |
| **10** | Tuân thủ (lưu giữ, giữ pháp lý, eDiscovery, xuất tenant); **bộ phân tích cú pháp IMAP dùng chung + máy khách IMAP tự viết (2.6.5)**; chuyển dữ liệu IMAP/mbox; xuất dữ liệu người dùng | Nhập thật từ một hộp Gmail và một hộp Outlook.com thử nghiệm; nhập lại cùng nguồn không trùng; mbox đọc được bằng công cụ ngoài |
| **11** | **BlueMail.Submission** (SMTP 465/587 AUTH) + **BlueMail.Imap**: IMAP4rev1 (RFC 3501) và các phần mở rộng `IDLE`, `UIDPLUS`, `MOVE`, `SPECIAL-USE`, `CONDSTORE`/`QRESYNC`, `LITERAL+`, `NAMESPACE`, `ID`, `ENABLE`, `COMPRESS=DEFLATE`; nhãn hiện như thư mục (một thư ở nhiều thư mục, giống Gmail IMAP); mật khẩu ứng dụng, OAUTHBEARER; autoconfig | `imaptest` đạt; Thunderbird, Outlook, iPhone Mail đọc/gửi/xoá/gắn nhãn đồng bộ hai chiều với web |
| **12** | Gửi ra ngoài quy mô thật: dải IP, làm ấm, giới hạn theo đích, xử lý bounce/ARF, tự khoá khi lạm dụng, TLS-RPT; **DNSSEC trong `BlueMail.Dns` + DANE**; chế độ bảo mật | `docs/07-kha-nang-gui-thu.md` hoàn chỉnh; DNSSEC khớp `dnspython` trên các vùng ký thật |
| **13** | Hoàn thiện: hiệu năng (đạt 8.2), truy cập, PWA/điện thoại, bản chạy thật, sao lưu/phục hồi thử thật, fuzz dài, tài liệu bàn giao | `docker compose up -d` lên hệ thống đầy đủ có dữ liệu mẫu |

### 13.1. Tài liệu trong `docs/`

`00-quyet-dinh-ky-thuat.md` · `01-huong-dan-su-dung.md` · `02-huong-dan-quan-tri.md` · `03-sao-luu-phuc-hoi.md`
· `04-cai-dat-va-dns.md` · `05-api.md` · `06-kich-ban-kiem-thu.md` · `07-kha-nang-gui-thu.md` · `08-so-loi.md`
(ghi thẳng mọi lỗi, kể cả lỗi do chính mình gây ra, có số đo/câu lệnh tái hiện) · `09-giay-phep-thanh-phan.md`
· `10-tuan-thu-rfc.md` (bảng mỗi RFC: mục nào đã làm, mục nào chưa, bằng chứng kiểm thử).

---

## 14. BÀI HỌC TỪ CÁC DỰ ÁN TRƯỚC — ĐỪNG LẶP LẠI

1. **Luật "một … một" phải là ràng buộc duy nhất ở CSDL**, và phép thử đồng thời phải gửi yêu cầu **thật sự song
   song**. Ở BlueMail: một nháp một lượt gửi, một hội thoại một lượt tạm ẩn, một tên miền một tenant, UID không
   trùng trong một nhãn, một người nhận một trạng thái giao.
2. **Công tắc được lưu không có nghĩa là có ai đọc nó.** Ô cấu hình nào cũng phải chỉ ra chỗ đọc; có phép thử quét.
3. **Cấu hình đọc một lần lúc khởi động là cấu hình không đổi được.** Ngưỡng rác, giới hạn tốc độ, lịch việc nền
   đổi trên màn hình phải có tác dụng ngay.
4. **Cột chép sẵn phải tính lại từ nguồn, không cộng dồn.** `used_bytes`, `legal_hold_count`, số chưa đọc.
5. **Tự kiểm bằng chính bộ mã của mình không chứng minh được gì.** SMTP với Postfix, DKIM với OpenDKIM/`dkimpy`,
   SPF với bộ phép thử chính thức, IMAP với `imaptest` và phần mềm thư thật.
6. **Giao thức nói chuyện với hệ thống khác phải trả lời sai một cách ồn ào**: lệnh SMTP/IMAP sai trả đúng mã lỗi;
   câu tìm kiếm sai báo lỗi, không trả cả hộp thư.
7. **Bộ đếm và danh sách phải chạy trên cùng một tập dòng**; phân trang sắp theo khoá duy nhất ở cuối.
8. **Cắt trước, lọc sau là sai.** Lọc trong truy vấn.
9. **Khoá một tài khoản phải khoá luôn phiên đang mở** — web, IMAP, SMTP — ở một chỗ.
10. **Mỗi lớp chặn phải nói cùng một thứ tiếng**: proxy trả 429 JSON; SMTP trả đúng mã `4xx`/`5xx` kèm mã mở rộng.
11. **"Đã lưu" chưa phải "hiện ra được".** Kiểm tới nơi người dùng nhìn thấy: web, Thunderbird, hộp thư người nhận
    bên ngoài.
12. **Đo ở đúng khổ đã cam kết**: 1366×768 và 375×812.
13. **Màu viết thẳng không đi qua token nào** — cấm bằng phép thử.
14. **Ảnh chụp màn hình không phải bằng chứng** — `getComputedStyle`, đo tương phản bằng máy.
15. **Giờ hiện cho người dùng là giờ của người dùng; giờ đi ra mạng là theo chuẩn RFC.**
16. **Việc gửi đi phải hỏi kênh gửi có mở không**; không báo "đã gửi" khi thư mới vào hàng đợi — giao diện phân biệt
    "đã gửi đi" và "đang chờ gửi".
17. **Dữ liệu mẫu là một phần của sản phẩm**: tên trùng, ngày viết cứng, ngày tương lai làm buổi trình diễn hỏng.
18. **Triển khai xong phải kiểm chính thứ vừa sửa trên máy chủ thật** — với thư: gửi thật tới một hộp Gmail và
    Outlook.com, xem tiêu đề `Authentication-Results` bên nhận.
19. **Mỗi lượt triển khai để lại một bộ image; ai không dọn thì ổ đĩa dọn.**
20. **Phông chữ phải có đủ glyph cho mọi ngôn ngữ đang bán**: dùng phông hệ thống theo từng hệ chữ, không ép một phông Latin cho mọi chữ (Georgia từng tách rời dấu tiếng Việt).
21. **Dọn theo kết cục, không theo cách thoát**: thư bị từ chối giữa chừng phải dọn khỏi bộ theo dõi EF và khỏi S3
    ở mọi nhánh — kể cả nhánh `return`, không chỉ nhánh `throw`.
22. **Ký tự rỗng U+0000 và chuỗi không hợp lệ UTF-8 sẽ đến từ Internet** — lọc ở cửa vào kho, không để PostgreSQL
    nổ giữa lúc giao thư.
23. **Mã tự viết đúng hay sai chỉ biết được khi so với mã của người khác.** Mọi thư viện nền tự viết (MIME, DNS,
    DKIM, IMAP, quét tệp) có phép đối chứng chạy thường xuyên với một cài đặt độc lập; mỗi lỗi do đối chứng bắt
    được thì thư gây lỗi được thêm vào bộ thư mẫu.

## 15. LƯU Ý CUỐI

1. **Không stub.** Không làm được thì dừng lại hỏi.
2. **Không bao giờ làm mất thư, không bao giờ làm relay mở.** Hai lỗi này giết một dịch vụ thư trong một ngày;
   mọi phase đều chạy lại phép thử của chúng.
3. **Bảo mật hiển thị thư là sống còn.** Một lỗ XSS trong khung đọc là kẻ gửi thư chiếm được hộp thư người đọc.
4. **Tự viết giao thức, không tự viết mật mã.** Mọi phép tính mật mã đi qua thư viện chuẩn của .NET.
5. **Uy tín IP gửi là tài sản.** Không bao giờ gửi thư thử nghiệm hàng loạt từ IP chạy thật.
6. **Test xanh không có nghĩa là chức năng đúng.** Mở hệ thống ra dùng như người dùng thật, bằng cả trình duyệt
   lẫn Thunderbird, gửi qua lại với Gmail và Outlook.com thật, và cố tình đi đường sai.
7. Mỗi phase xong: đối chiếu với tài liệu này và với `docs/10-tuan-thu-rfc.md`, ghi rõ cái gì đạt, cái gì còn thiếu.
