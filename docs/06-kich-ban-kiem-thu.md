# 06 — Kịch bản kiểm thử (Test scenarios)

## Môi trường kiểm thử dev

```sh
# Khởi động (profile phase0 thêm bộ hứng thư ở 10.77.0.25 cho tới khi có BlueMail.Mx)
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev --profile phase0 up -d --build
MSYS_NO_PATHCONV=1 sh scripts/s3-init.sh          # tạo bucket + khoá S3 (idempotent)
MSYS_NO_PATHCONV=1 sh scripts/dev-phase0-check.sh     # nghiệm thu Phase 0
python scripts/dev-gen-zones.py                       # sinh lại vùng DNS khi đổi IP/khoá
python tests/BlueMail.Corpus/generate.py              # sinh lại bộ thư mẫu
```

(`MSYS_NO_PATHCONV=1` chỉ cần trên Windows/Git Bash.)

## Lệnh công cụ đối chứng

| Mục đích | Lệnh (chạy trong container `tools`) |
|---|---|
| Gửi SMTP | `swaks --server <ip> --from a@doitac.test --to b@bluemail.test --tls` |
| Thư cỡ lớn | `swaks ... --attach-type application/octet-stream --attach @/work/big.bin` |
| BDAT | `swaks ... --pipe` không hỗ trợ BDAT; dùng `python /work/tests/interop/smtp_bdat.py` (Phase 3) |
| Kiểm DKIM | `python -c "import dkim,sys;print(dkim.verify(open(sys.argv[1],'rb').read()))" thư.eml` |
| Kiểm SPF/DKIM/DMARC đầy đủ | `python -m authheaders` qua `authheaders.authenticate_message(...)` |
| Đọc bản ghi DMARC/SPF | `checkdmarc doitac.test -n 10.77.0.53` (tên miền đặt **trước** `-n`) |
| Tra DNS đối chứng | `dig @10.77.0.53 ...`, `python -c "import dns.resolver ..."` |
| Đọc mbox | `python -c "import mailbox; print(len(mailbox.mbox('x.mbox')))"` |
| Đọc vCard | `python -c "import vobject; [print(c.fn.value) for c in vobject.readComponents(open('x.vcf'))]"` |
| Quét so sánh | `docker compose ... --profile scanner-compare run --rm --entrypoint clamscan clamav -r /samples` |
| IMAP tuân thủ (Phase 11) | `docker compose ... --profile imap-compliance run --rm imaptest host=10.77.0.28 port=143 user=... pass=...` |

## Bảng kịch bản

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P0-01 | DNS dev | `dig MX bluemail.test` qua CoreDNS | `10 mx.bluemail.test.` | như mong đợi (2026-11-23) | ✅ |
| P0-02 | PTR dev | `dig -x 10.77.0.26` | `out1.bluemail.test.` | như mong đợi | ✅ |
| P0-03 | MX rỗng | `dig MX nullmx.doitac.test` | `0 .` | như mong đợi | ✅ |
| P0-04 | Cắt UDP | `dig +notcp +bufsize=1232 TXT bigtxt.bluemail.test` | cờ `tc` | như mong đợi | ✅ |
| P0-05 | Postfix gửi ra | swaks → Postfix → MX bluemail.test | bộ hứng nhận, `dkimpy` → True | như mong đợi | ✅ |
| P0-06 | Postfix nhận vào | swaks → `binh@doitac.test` | có trong Maildir, có `Authentication-Results` | như mong đợi | ✅ |
| P0-07 | Lỗi tạm cố định | `RCPT TO:<tempfail@doitac.test>` | `451 4.3.0` | như mong đợi | ✅ |
| P0-08 | Lỗi vĩnh viễn cố định | `RCPT TO:<reject@doitac.test>` | `550 5.1.1` | như mong đợi | ✅ |
| P0-09 | checkdmarc | `checkdmarc doitac.test -n 10.77.0.53` | đọc được `p=reject` | như mong đợi | ✅ |
| P0-10 | Bộ thư mẫu | `generate.py` | ≥ 40 thư, đọc được bằng `email` của Python | 46 thư | ✅ |
| P0-11 | imaptest, ClamAV | chạy container theo profile | chạy được | `imaptest` chạy; ClamAV 1.5.5/28191 | ✅ |

## Phase 1

Chạy: `dotnet test BlueMail.slnx` (unit + tích hợp với PostgreSQL và Valkey thật qua Testcontainers);
`cd web && npm test`. Chạy tay: `scripts/dev-run.sh identity`, `scripts/dev-run.sh api`, `cd web && npm run dev`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P1-01 | Đăng nhập web | Mở web → chuyển BlueID → nhập `lan@bluemail.test` | Về `/mail/inbox`, giao diện tiếng Việt, tên tổ chức trên thanh trên | Như mong đợi (Chrome, 1366×768, 2026-11-23) | ✅ |
| P1-02 | Đăng nhập qua bản chạy thật | `docker compose -f deploy/docker-compose.yml` + `oidc_smoke.py` | `/api/v1/me` 200, HSTS, nosniff, CSP | Như mong đợi sau khi sửa BUG-004/005 | ✅ |
| P1-03 | Sai mật khẩu | POST đăng nhập sai | Ở lại trang, ghi `login_events` reason `bad_password` | `Wrong_password_stays_on_login_page_and_is_recorded` | ✅ |
| P1-04 | Khoá tài khoản tức thì | Quản trị viên tạm khoá người đang cầm thẻ | Thẻ đó nhận 401 ngay, đăng nhập lại bị từ chối | `Suspending_a_user_invalidates_the_token_already_held_immediately` | ✅ |
| P1-05 | Cách ly tenant | Quản trị viên acme gọi các endpoint với mã của bluemail | 404 (hoặc 403 với endpoint nền tảng), dữ liệu không đổi | `Ids_of_another_tenant_answer_404_on_every_endpoint` | ✅ |
| P1-06 | Bộ lọc đóng mặc định | Truy vấn khi chưa đặt tenant | 0 dòng | `Global_filter_returns_nothing_when_no_tenant_is_set` | ✅ |
| P1-07 | UID/modseq song song | 40 yêu cầu thật song song | Không trùng, liên tục | `Parallel_uid…`, `Parallel_modseq…` | ✅ |
| P1-08 | Một tên miền một tenant | 3 yêu cầu song song tạo cùng tên miền | 1 thành công, 2 `domain_taken` | `One_domain_belongs_to_one_tenant…` | ✅ |
| P1-09 | Nhật ký chuỗi băm | 25 ghi song song, rồi `UPDATE`/`DELETE` | Chuỗi hợp lệ; sửa/xoá bị CSDL từ chối | `Audit_chain_stays_valid…` | ✅ |
| P1-10 | NUL ở cửa vào | `?search=a%00b` | 400 `invalid_characters` | `Nul_characters_in_query_are_refused_at_the_door` | ✅ |
| P1-11 | Lỗi theo ngôn ngữ người dùng | Người dùng tiếng Việt gọi endpoint quản trị | 403, `code` ổn định, `title` tiếng Việt | `Error_text_follows_the_user_language…` | ✅ |
| P1-12 | CORS BlueID | Preflight từ gốc web và gốc lạ | Gốc web được phép, gốc lạ không | `BlueID_allows_cross_origin_calls…` | ✅ |
| P1-13 | DI đầy đủ | Khởi động BlueID/API ở Development (ValidateOnBuild), khởi động lần hai | Không lỗi; nạp lại dữ liệu mẫu không trùng | `HostStartupTests` | ✅ |
| P1-14 | Khổ 375×812 | Giả lập điện thoại, mở danh sách người dùng | Không cuộn ngang trang; cột nhãn đóng | `scrollWidth` 375, cột 0 px | ✅ |
| P1-15 | Tương phản | Mọi cặp token chữ/nền, sáng và tối | ≥ 4,5:1 | `tokens.test.ts` (18 cặp) | ✅ |
| P1-16 | Giờ theo múi người dùng | 20:30Z hiển thị cho Asia/Ho_Chi_Minh | 03:30 ngày hôm sau, không giờ UTC; đúng cả ngày đổi giờ châu Âu | `format.test.ts` | ✅ |

## Phase 2 — thư viện nền tự viết

Chạy: `dotnet test tests/BlueMail.InteropTests` (cần mạng dev đang chạy: container `tools`, CoreDNS, Postfix;
từ Phase 3 thư tới bluemail.test do `mx` nhận); `dotnet test tests/BlueMail.FuzzTests` (mặc định 1 500 lần/đích; chạy dài:
`BM_FUZZ_ITERATIONS=100000 BM_FUZZ_SEED=<n>`). Fuzz dài bằng libFuzzer: xem `tests/BlueMail.FuzzTests/Program.cs`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P2-01 | MIME ↔ Python `email` | `MimeCorpusTests` trên 46 thư mẫu | Khớp tiêu đề, địa chỉ, ngày, Message-ID, cây phần, tên tệp, băm nội dung, chữ | Khớp, trừ 8 khác biệt có lý do trong `python-differences.json` | ✅ |
| P2-02 | Thư dựng ra đọc được | `MimeBuilderInteropTests` (5 kiểu thư) | Python đọc đúng tiêu đề/tên/tệp/băm; dòng ≤ 998; CRLF | Như mong đợi | ✅ |
| P2-03 | Postfix nhận thư dựng ra | swaks gửi thư có tệp tên Unicode + 300 KB | Giao vào Maildir, Python đọc đúng 3 tệp | Như mong đợi | ✅ |
| P2-04 | DNS ↔ dnspython | 21 truy vấn trên CoreDNS | Cùng trạng thái (Ok/NoData/NxDomain), cùng bản ghi, cùng tên chuẩn | Như mong đợi | ✅ |
| P2-05 | Cắt UDP → TCP | TXT 40 bản ghi, UDP 512 | Đủ 40 bản ghi | Như mong đợi | ✅ |
| P2-06 | DKIM của mình → dkimpy | ký 3 kiểu chuẩn hoá | dkimpy `pass`; sửa 1 byte → `fail`; thêm From → `fail` | Như mong đợi | ✅ |
| P2-07 | DKIM của dkimpy/OpenDKIM → mình | dkimpy ký 3 kiểu; Postfix+OpenDKIM ký thật | `pass`, sửa byte → `fail` | Như mong đợi | ✅ |
| P2-08 | ARC | mình niêm 1 và 2 chặng; dkimpy niêm | Hai bên `pass`; sửa → `fail` | Như mong đợi | ✅ |
| P2-09 | Bộ quét: mẫu nguy hiểm | 30 mẫu tự dựng (PE giả, macro, mẫu từ xa, DDE, PDF JS/Launch, smuggling, bom nén, RLO…) | Đúng mức và đúng mã lý do | 30/30 | ✅ |
| P2-10 | Bộ quét: mẫu sạch | 15 mẫu (PDF, ảnh, docx/xlsx, zip, csv, văn bản nhiều ngôn ngữ, tar/gz…) | `Clean` | 15/15 | ✅ |
| P2-11 | So với ClamAV | Đưa 45 mẫu vào container ClamAV qua stdin | Có báo cáo khác biệt; ClamAV thấy EICAR | ClamAV chỉ bắt EICAR và DDE (theo chữ ký); BlueMail bắt các mẫu cấu trúc; khác biệt ngược duy nhất `eicar.txt` (lớp 8, Phase 6) | ✅ |
| P2-12 | Fuzz | 5 đích × 100 000 lần × 3 hạt giống | Không ngoại lệ ngoài danh sách, không treo, bộ nhớ < 1,5 GB | Sạch sau khi sửa BUG-011..015 do fuzz tìm ra | ✅ |

## Phase 3 — kho thư và nhận thư (BlueMail.Mx)

Chạy: `sh scripts/dev-phase3-check.sh` (mạng dev có `mx` ở 10.77.0.25; CSDL đã migrate + seed bằng
`scripts/dev-run.sh identity` một lần). Phép thử đơn vị: `--filter Smtp|MailStore`; tích hợp:
`MailDeliveryTests` (PostgreSQL, Valkey, Garage S3 thật trong container). Máy khách đối chứng: Postfix (doitac.test),
swaks, Python `smtplib` và socket thô (`tests/interop/mx_checks.py`).

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P3-01 | Postfix → mx | swaks gửi qua Postfix của doitac.test (ký DKIM, có trong SPF) tới `minh@` | Lưu đúng 1 bản, `spf=pass dkim=pass dmarc=pass`, có UID IMAP trong INBOX của minh | Như mong đợi | ✅ |
| P3-02 | Không relay mở | RCPT tới `someone@example.org` | 550 5.7.1 tại RCPT | 550 | ✅ |
| P3-03 | Người nhận không tồn tại | RCPT `no.such.user@bluemail.test` | 550 5.1.1 ngay tại RCPT (không nhận rồi trả lại) | 550, ghi `admin.mail_log` | ✅ |
| P3-04 | Bí danh, nhóm, địa chỉ phụ | `sales@`, `kinhdoanh@`, `lan+news@` | 250; nhóm + thành viên + bí danh trong 1 giao dịch → mỗi hộp 1 bản (4 hộp) | 4 bản / 4 hộp; cùng 1 blob (khử trùng SHA-256) | ✅ |
| P3-05 | Quá cỡ | `SIZE=` vượt giới hạn; DATA thật vượt 50 MB | 552 tại MAIL; 552 sau dấu chấm, không lưu gì | Như mong đợi | ✅ |
| P3-06 | SMTPUTF8 | Phong bì `người.gửi@ví-dụ.test`, tiêu đề tiếng Việt | Nhận; thiếu tham số SMTPUTF8 → 553 5.6.7 | Như mong đợi (sau BUG-017) | ✅ |
| P3-07 | BDAT | 2 khúc, có dòng bắt đầu bằng dấu chấm | 250 mỗi khúc; nội dung nguyên vẹn (không xử lý dấu chấm) | Như mong đợi | ✅ |
| P3-08 | PIPELINING | MAIL+RCPT+RCPT(sai)+DATA trong 1 gói | Trả lời đúng thứ tự 250,250,550,354; chỉ người nhận hợp lệ được lưu | Như mong đợi | ✅ |
| P3-09 | Tiêm lệnh STARTTLS | `STARTTLS\r\nMAIL FROM:…` trong 1 gói | Lệnh tiêm bị bỏ; trong TLS (≥1.2) phải EHLO lại; không còn quảng bá STARTTLS | Như mong đợi (cả phép thử socket trong UnitTests) | ✅ |
| P3-10 | Nói trước lời chào | Gửi EHLO trước banner | 554 5.7.1 và đóng | Như mong đợi | ✅ |
| P3-11 | DMARC p=reject | Giả `From: an@doitac.test` từ máy ngoài SPF, không ký | 550 5.7.1 trong phiên, không lưu, ghi mail_log | Như mong đợi | ✅ |
| P3-12 | Tệp nguy hiểm | Đính kèm PE giả `hoa-don.pdf.exe` | 554 5.7.1 trong phiên, không lưu | Như mong đợi | ✅ |
| P3-13 | Lỗi lặp lại | 10 lệnh sai | 421 và đóng kết nối | Như mong đợi | ✅ |
| P3-14 | Mã hoá S3 | `MailDeliveryTests` | Đối tượng trên S3 không chứa bản rõ; đọc lại đủ và theo khoảng (3 khúc 64 KiB); tenant khác không mở được | Như mong đợi; đổi bit, đổi thứ tự khúc, cắt cụt → `CryptographicException` | ✅ |
| P3-15 | Luồng thư | References/In-Reply-To; tiền tố `TL: Re:`; cùng tiêu đề không tiền tố | 2 trường hợp đầu vào cùng luồng; trường hợp 3 luồng mới | Như mong đợi | ✅ |
| P3-16 | Giao song song | 24 thư cùng lúc vào 1 hộp | UID và modseq không trùng, `uid_next` > UID lớn nhất | Như mong đợi | ✅ |
| P3-17 | Tìm kiếm đa ngôn ngữ | "hop dong" / "HỢP ĐỒNG" / "签署" | Khớp không dấu, không phân biệt hoa thường, chuỗi con CJK | Như mong đợi | ✅ |
| P3-18 | Sự kiện thay đổi | Theo dõi kênh Valkey `bm:mbx:<id>` | Nhận `message.added`; giao vào hộp của tenant khác → từ chối | Như mong đợi | ✅ |
| P3-19 | Fuzz | Đích `smtp` (khung dòng, DATA, BDAT, máy trạng thái) và `text` × 100 000 × 3 hạt giống | Không ngoại lệ, không treo | Sạch sau khi sửa BUG-020 (fuzz `text` tìm ra phi ký tự Unicode làm hỏng giao thư) | ✅ |

## Phase 4 — web đọc thư

Chạy: `dotnet test --filter "MailReadingApi|UnitTests.Mail"`; web `npx vitest run`. Kiểm thật trên trình duyệt:
`scripts/dev-run.sh identity` + `api`, `npm run dev`, gửi thư mẫu qua mx bằng
`docker exec bluemail-dev-tools-1 python /work/scripts/dev-seed-mail.py` (thư doitac.test đi qua Postfix của nó).

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P4-01 | Danh sách hội thoại | Mở Hộp thư đến (lan, tiếng Việt) | Thư nhiều ngôn ngữ (vi, ja, zh, th, ar, ru), hội thoại gộp có số thư, thẻ tệp, giờ theo Asia/Ho_Chi_Minh | Như mong đợi | ✅ |
| P4-02 | Phân trang con trỏ | API `limit=2` | Hai trang không trùng, mới nhất trước, `total` đúng | Như mong đợi | ✅ |
| P4-03 | HTML an toàn | Thư có `<script>`, `onclick`, `javascript:`, pixel theo dõi, ảnh `cid:` | Mã và sự kiện bị bỏ; ảnh cid hiện qua URL ký; pixel không tải; iframe sandbox không script, CSP chặt | Như mong đợi (kiểm cả 16 vectơ XSS ở UnitTests) | ✅ |
| P4-04 | Ảnh từ xa | "Hiển thị hình ảnh" / "Luôn hiển thị…" | Ảnh đi qua `/api/v1/mail/img/…`, trình duyệt không gọi máy chủ ngoài; tin cậy người gửi được nhớ | Như mong đợi (ghi lại yêu cầu mạng) | ✅ |
| P4-05 | Chặn SSRF | Địa chỉ nội bộ, metadata đám mây, DNS trỏ về 127.0.0.1, NAT64/6to4, cổng lạ, file:// | Từ chối | Như mong đợi | ✅ |
| P4-06 | Liên kết lừa | Chữ hiện `https://www.shop.test/account`, đích `evil.test` | Viền cảnh báo + tiêu đề `⚠ https://evil.test/login` | Như mong đợi | ✅ |
| P4-07 | Biểu ngữ | Người gửi ngoài; tên miền giả `bluemaiI.test`; DMARC fail; tệp nguy hiểm | Đúng biểu ngữ | Như mong đợi (giả dạng và người ngoài kiểm trên trình duyệt; còn lại ở phép thử) | ✅ |
| P4-08 | Tệp đính kèm | PDF, txt, PNG; tệp nhiễm | Xem PDF bằng pdf.js, tải về qua URL ký, ZIP tất cả; tệp nhiễm không có liên kết tải, URL giả mạo → 404 | Như mong đợi | ✅ |
| P4-09 | Thao tác | Đọc/chưa đọc, sao, nhãn mới, gắn nhãn 2 hội thoại, lưu trữ, thùng rác, xoá vĩnh viễn | Danh sách và số đếm cập nhật; xoá vĩnh viễn ngoài Thùng rác bị từ chối; nhật ký label_changes cho IMAP | Như mong đợi | ✅ |
| P4-10 | Thời gian thực | Gửi thư mới qua Postfix khi trang đang mở | Thư hiện ngay, số chưa đọc và tiêu đề tab cập nhật, không tải lại trang | Như mong đợi; hub từ chối hộp thư tenant khác | ✅ |
| P4-11 | Đồng bộ tăng dần | `/mail/changes?sinceModseq=` | Trả thư đổi cờ/nhãn và id đã xoá | Như mong đợi | ✅ |
| P4-12 | Cô lập | Người cùng tenant và tenant khác mở/đổi hội thoại của người khác | 404; thao tác bỏ qua id lạ (0 thư đổi) | Như mong đợi | ✅ |
| P4-13 | Bố cục | 1400 px khung đọc bên phải; 375 px | Chia đôi; ở 375 px một cột, không tràn ngang | Như mong đợi sau BUG-023, BUG-024 | ✅ |

Chưa làm trong Phase 4 (đúng phase sau theo mục 13): trả lời/chuyển tiếp và phản hồi lời mời lịch (Phase 5); nút
hủy đăng ký, tạm ẩn, biểu ngữ "người gửi lần đầu" (cần danh bạ), phím tắt (Phase 8); thao tác hàng loạt > 500 thư
chạy nền (cần Hangfire, Phase 7/8 khi có việc nền đầu tiên) — hiện API giới hạn 500/lần và giao diện báo rõ.

## Phase 5 — gửi thư (soạn, nháp, BlueMail.Outbound)

Chạy: `dotnet test --filter "SendingTests|RoutingTests|DsnTests|SmtpClientTests"`; web `npx vitest run`. Kiểm thật:
`docker compose … up -d --build mx outbound` (outbound = out1.bluemail.test, 10.77.0.26), `dev-run.sh identity|api`,
soạn trên web gửi tới `binh@doitac.test`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P5-01 | Gửi ra ngoài qua Postfix | Web: lan@ soạn (chữ đậm, tiêu đề tiếng Việt) tới binh@doitac.test + minh@ | Postfix nhận qua TLS, chữ ký DKIM `d=bluemail.test s=bm2026` hợp lệ; minh nhận bản nội bộ; 1 bản trong Đã gửi; nháp biến mất | Như mong đợi; DKIM kiểm bằng dkimpy trên tệp Maildir (`True`) vì OpenDKIM của container đối chứng không phân giải DNS được (BUG-027) | ✅ |
| P5-02 | Hoàn tác | Gửi (10 s) → Hoàn tác | Thư không đi; nháp mở lại y nguyên | Như mong đợi (API + giao diện) | ✅ |
| P5-03 | Không gửi hai lần | Bấm gửi hai lần; hai bộ điều phối chạy song song; giả lập chết sau khi nộp | 409 `already_sending`; 1 bản Đã gửi, 1 bản mỗi hộp nội bộ, 1 dòng hàng đợi | Như mong đợi | ✅ |
| P5-04 | Hẹn giờ | Hẹn 3 giờ sau; huỷ | Nằm ở "Đã lên lịch", chưa gửi; huỷ → về "Thư nháp" | Như mong đợi | ✅ |
| P5-05 | Kết quả theo người nhận | Máy nhận giả: ok/550 5.1.1/451 | Giao / trả lại (+ danh sách chặn gửi + DSN tiếng Anh + tiếng của người gửi) / hoãn 5 phút | Như mong đợi; thử lại đúng lịch (chưa tới hạn không thử) | ✅ |
| P5-06 | Báo trễ và hết hạn | Lỗi tạm mãi | Sau 4 giờ: DSN "Delay"; sau 5 ngày: EXPIRED + DSN "Failure" | Như mong đợi | ✅ |
| P5-07 | Tiến trình bị giết | Máy nhận giữ trả lời cuối, giết bộ giao | Thư chưa tính là đã giao; tiến trình khác không lấy được trong thời hạn thuê; hết hạn → giao đúng 1 lần | Như mong đợi | ✅ |
| P5-08 | Định tuyến | MX theo ưu tiên, không MX, MX rỗng, NXDOMAIN, lỗi DNS | Thứ tự đúng; MX ngầm định; trả lại ngay; trả lại; hoãn (không trả lại vì DNS chập chờn) | Như mong đợi | ✅ |
| P5-09 | MTA-STS | Phân tích chính sách; chế độ enforce với chứng chỉ không tin cậy | Ký tự đại diện chỉ một nhãn; không bao giờ rơi về văn bản trần | Như mong đợi | ✅ |
| P5-10 | Máy khách SMTP | Với máy chủ BlueMail (TLS, BDAT) và máy chủ tối giản (DATA) | Byte nhận khớp từng byte; dấu chấm đầu dòng được nhân đôi | Như mong đợi | ✅ |
| P5-11 | Từ, trả lời, chuyển tiếp | Gửi từ địa chỉ người khác; trả lời tất cả; chuyển tiếp | 403; Re:/Fwd: một lần, In-Reply-To/References, cc không gồm mình; thư trả lời vào cùng hội thoại | Như mong đợi | ✅ |
| P5-12 | Soạn trên web | Chip người nhận (ngoài tổ chức đổi màu), gợi ý từ tổ chức, tự lưu nháp, định dạng | Như mong đợi | Kiểm trên trình duyệt | ✅ |
| P5-13 | Cảnh báo trước khi gửi, hẹn giờ theo múi giờ | `compose.test.ts` | Tiêu đề trống, nhắc đính kèm (bỏ qua phần trích dẫn), gửi nhiều ra ngoài; 8:00 sáng mai đúng múi giờ người dùng, qua DST | Như mong đợi | ✅ |

Chưa làm trong Phase 5 (đúng phase sau): dải IP và làm ấm, bounce/ARF không đồng bộ, VERP, TLS-RPT, DANE (Phase 12);
SMTP nộp thư 587/465 (Phase 11); chế độ bảo mật (Phase 12).
