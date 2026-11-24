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
chạy nền (cần Hangfire, Phase 7/8 khi có việc nền đầu tiên) — hiện API giới hạn 500/lần và giao diện báo rõ. **Đã làm 2026-11-24:** việc nền `mail.bulk_jobs` (P13-15).

## Phase 5 — gửi thư (soạn, nháp, BlueMail.Outbound)

Chạy: `dotnet test --filter "SendingTests|RoutingTests|DsnTests|SmtpClientTests"`; web `npx vitest run`. Kiểm thật:
`docker compose … up -d --build mx outbound` (outbound = out1.bluemail.test, 10.77.0.26), `dev-run.sh identity|api`,
soạn trên web gửi tới `binh@doitac.test`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P5-01 | Gửi ra ngoài qua Postfix | Web: lan@ soạn (chữ đậm, tiêu đề tiếng Việt) tới binh@doitac.test + minh@ | Postfix nhận qua TLS, chữ ký DKIM `d=bluemail.test s=bm2026` hợp lệ; minh nhận bản nội bộ; 1 bản trong Đã gửi; nháp biến mất | Như mong đợi: Postfix ghi `dkim=pass (Good 2048 bit rsa-sha256 signature) header.d=bluemail.test` (bộ kiểm dkimpy-milter, sau BUG-027) | ✅ |
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

## Phase 6 — lọc rác và quét tệp đầy đủ

Chạy: `dotnet test --filter "Antispam|Scanner"` (đơn vị), `dotnet test tests/BlueMail.IntegrationTests --filter AntispamTests`,
web `npx vitest run`. Kiểm thật: `docker compose … up -d --build mx`, `sh scripts/dev-phase6-check.sh`
(gửi bằng `tests/interop/spam_checks.py` từ container tools) và `sh scripts/dev-phase3-check.sh` (không bắt nhầm thư thật).
Bộ thư đo chất lượng: `tests/BlueMail.SpamCorpus/generate.py` → `corpus.jsonl` (1 400 thư tự dựng, đa ngôn ngữ),
kết quả mới nhất ghi ở `tests/BlueMail.SpamCorpus/last-report.txt` mỗi lần chạy `SpamQualityTests`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P6-01 | Chất lượng lọc rác | `SpamQualityTests`: kiểm chéo 2 phần (Bayes học nửa này, chấm nửa kia), ngưỡng 5 | Bắt nhầm < 0,1 %, bắt đúng ≥ 95 % | Bắt đúng **697/700 (99,6 %)**, bắt nhầm **0/700 (0 %)**; điểm cao nhất của thư thật −0,5 | ✅ |
| P6-02 | Chất lượng quét tệp | `ScannerSampleTests` (lớp 1–8, gồm danh sách băm và luật mẫu byte) | Chặn đủ mẫu nguy hiểm, không chặn mẫu sạch | **31/31** mẫu nguy hiểm bị chặn đúng lý do; **0/14** mẫu sạch bị chặn | ✅ |
| P6-03 | Thư thật từ người lạ | `spam_checks.py`: thư tiếng Việt bình thường, không SPF/DKIM | Vào Hộp thư đến | Như mong đợi | ✅ |
| P6-04 | Lừa đảo giả tên miền | Tên miền `bluemaiI-support.test`, tên hiển thị "bluemail.test IT", Reply-To gmail, liên kết che | Vào Thư rác; lưu điểm và bảng luật (`FROM_LOOKALIKE_DOMAIN`…) | Như mong đợi sau BUG-033 (điểm 13,6) | ✅ |
| P6-05 | Từ chối trong phiên | Thư vượt ngưỡng từ chối (15) | `550 5.7.1 Message rejected as spam`; không lưu; mail_log ghi điểm | Như mong đợi (điểm 20,6) | ✅ |
| P6-06 | Học từ nút báo cáo | 22 thư "Báo cáo thư rác" (bấm 2 lần) + 22 "Không phải thư rác"; rồi đổi ý 2 thư | Mô hình người dùng 22/22 (không đếm trùng); sau đổi ý 20/24; Bayes > 0,9 cho nội dung tương tự | Như mong đợi | ✅ |
| P6-07 | Báo cáo lừa đảo | Người dùng bấm "Báo cáo lừa đảo"; quản trị đặt hộp thư bảo mật | Thư vào Thư rác, bản sao (đính kèm nguyên văn) tới hộp thư bảo mật; địa chỉ ngoài tổ chức bị từ chối; người thường không đọc được cài đặt (403) | Như mong đợi | ✅ |
| P6-08 | Cách ly | Hai quản trị viên cùng "Thả" một thư cách ly | Thư tới người nhận đúng 1 lần, lần thứ hai 409; ghi nhật ký kiểm toán | Như mong đợi | ✅ |
| P6-09 | Greylisting | Bộ ba mới; thử lại sớm; thử lại sau 5 phút; cùng /24 | Hoãn, hoãn, qua, qua | Như mong đợi | ✅ |
| P6-10 | "Vì sao thư này ở Thư rác?" | Mở thư trong Thư rác | Biểu ngữ + bảng lý do bằng ngôn ngữ người dùng, điểm từng luật | Như mong đợi; mọi mã luật có bản dịch en/vi (phép thử C# + vitest) | ✅ |
| P6-11 | DNSBL | Danh sách nguồn | Mặc định tắt hết, có ghi chú giấy phép; bộ chấm chạy được khi không có nguồn nào | Như mong đợi (P6-01 chạy không DNSBL) | ✅ |

Chưa làm trong Phase 6 (đúng phase sau): trừ điểm người gửi có trong danh bạ (danh bạ ở Phase 8); giao diện quản trị
cho cài đặt rác, danh sách chặn và cách ly (Phase 9 — API đã có); quản trị viên xem bảng điểm ở tìm vết thư (Phase 9).

## Phase 7 — tìm kiếm toán tử, bộ lọc, chặn, trả lời tự động, chuyển tiếp

Chạy: `dotnet test --filter "Search|Rules"` (đơn vị), `dotnet test tests/BlueMail.IntegrationTests --filter RulesTests`,
web `npx vitest run`. Kiểm thật qua SMTP: `docker compose … up -d --build mx outbound`, `sh scripts/dev-phase7-check.sh`
(gửi qua Postfix của doitac.test bằng `tests/interop/rules_checks.py`, đọc Maildir của doitac).

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P7-01 | Bộ phân tích toán tử | Mỗi toán tử ở mục 6.7; 27 câu sai | Cây đúng; câu sai báo mã lỗi rõ (ngoặc, ngày, kích thước, OR treo, khoảng ngày ngược…), không coi là từ khoá | Như mong đợi (`SearchParserTests`, 84 ca) | ✅ |
| P7-02 | Một luật, hai lối | 33 câu trên 4 thư đa ngôn ngữ | SQL tìm kiếm và bộ so khớp của bộ lọc cho cùng kết quả | Như mong đợi | ✅ |
| P7-03 | Tìm kiếm trên web | Ô tìm kiếm, `(bao gia` | Kết quả; câu sai hiện lý do bằng tiếng của người dùng | Như mong đợi sau BUG-035 | ✅ |
| P7-04 | Tìm kiếm nâng cao | Biểu mẫu → chuỗi toán tử; "Tạo bộ lọc"; 375 px | Đúng chuỗi; mở bộ lọc với tiêu chí; trên điện thoại là hộp thoại | Như mong đợi sau BUG-040 | ✅ |
| P7-05 | Bộ lọc qua SMTP | Postfix → mx, bộ lọc "from:… subject:…" bỏ hộp thư đến + đọc + sao | Thư khớp: không ở Hộp thư đến, đã đọc, gắn sao; thư không khớp ở Hộp thư đến | Như mong đợi | ✅ |
| P7-06 | Áp dụng cho thư có sẵn | Lưu bộ lọc, chọn "áp dụng cho N thư" | Chạy nền, có tiến độ; kết quả như lúc giao thư | Như mong đợi (`FilterJobWorker`, SKIP LOCKED + lease) | ✅ |
| P7-07 | Gmail XML | Nhập tệp xuất của Gmail; xuất rồi nhập lại | Tiêu chí thành câu toán tử, nhãn tạo nếu thiếu, mục không hỗ trợ được báo; DOCTYPE bị từ chối | Như mong đợi | ✅ |
| P7-08 | Chặn người gửi | Chặn binh@doitac.test; gửi qua Postfix | Vào Thư rác; bộ lọc "không bao giờ là rác" không kéo ra được | Như mong đợi | ✅ |
| P7-09 | Trả lời tự động | Bật; gửi 2 thư cùng người; thư danh sách; thư Auto-Submitted | Đúng 1 trả lời, `MAIL FROM:<>`, `Auto-Submitted: auto-replied`, In-Reply-To đúng; không trả lời danh sách/thư tự động | Như mong đợi sau BUG-037 | ✅ |
| P7-10 | Chuyển tiếp có xác minh | Thêm địa chỉ → mã 6 số gửi tới địa chỉ; nhập sai/đúng; bật | Chưa xác minh không bật được; sai mã 400; đúng mã bật được | Như mong đợi | ✅ |
| P7-11 | Chuyển tiếp thật | ngoc → binh@doitac.test qua Outbound | Postfix nhận: Return-Path SRS0, ARC-Seal i=1 d=bluemail.test, DKIM gốc của doitac.test vẫn pass, có dấu chống vòng | Như mong đợi | ✅ |
| P7-12 | Chống vòng, chính sách | Thư đã có dấu của hộp thư; quản trị cấm chuyển ra ngoài | Không chuyển tiếp lại; chuyển tiếp ra ngoài dừng, thêm địa chỉ ngoài bị 403 | Như mong đợi | ✅ |
| P7-13 | SRS ngược | Bounce tới địa chỉ SRS; người gửi thường; địa chỉ giả; chuyển tiếp tới địa chỉ không tồn tại | Bounce chuyển về người gửi gốc; 550 cho người gửi thường và hash sai; DSN của Outbound về người gửi gốc | Như mong đợi sau BUG-039 | ✅ |

Chưa làm trong Phase 7 (đúng phase sau): ~~gợi ý khi gõ ở ô tìm kiếm (danh bạ ở Phase 8)~~ — đã làm 2026-11-24 (tìm gần đây, nhãn, người liên hệ; không dấu; `search.test.ts`, thử ở 375 px), "chỉ trả lời danh bạ" dùng
danh bạ thật (Phase 8), giao diện quản trị chính sách chuyển tiếp (Phase 9 — API đã có).

## Phase 8 — sắp xếp, danh bạ, cài đặt, phím tắt

Chạy: `dotnet test --filter "Contacts|Rules"` (đơn vị), `dotnet test tests/BlueMail.IntegrationTests --filter OrganizeTests`,
web `npx vitest run`; kiểm trên trình duyệt (desktop và 375 px).

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P8-01 | Tab Hộp thư đến | Thư từ facebookmail.com, thư có List-Unsubscribe, có List-Id, từ no-reply@ | Mạng xã hội, Quảng cáo, Diễn đàn, Cập nhật | Như mong đợi | ✅ |
| P8-02 | Danh bạ thắng | Người trong danh bạ gửi thư có List-Unsubscribe | Vào Chính | Như mong đợi | ✅ |
| P8-03 | Quan trọng tự động | Đồng nghiệp gửi thẳng; người mình gửi ≥ 3 lần; thư hàng loạt của người đó | Quan trọng, quan trọng, không quan trọng | Như mong đợi | ✅ |
| P8-04 | Kéo sang tab khác | Kéo hội thoại sang "Quảng cáo" | Chuyển tab; hỏi tạo quy tắc → bộ lọc `from:(…)` → tab | Như mong đợi (trình duyệt) | ✅ |
| P8-05 | Tắt tiếng | Tắt tiếng; trả lời gửi cho nhóm; trả lời gửi thẳng cho mình | Rời Hộp thư đến; ở ngoài; vào Hộp thư đến | Như mong đợi | ✅ |
| P8-06 | Tạm ẩn | Tạm ẩn 3 giờ; tới giờ (worker); thư mới trong hội thoại đang tạm ẩn | Vào "Đã tạm ẩn" với giờ quay lại; về Hộp thư đến chưa đọc; về ngay | Như mong đợi; giờ đặt sẵn theo múi giờ người dùng (`snooze.test.ts`) | ✅ |
| P8-07 | Hủy đăng ký | mailto:; https + List-Unsubscribe-Post (RFC 8058) có DKIM pass; không có tiêu đề | Gửi thư hủy; POST một chạm; 400 rõ lý do | mailto và lỗi kiểm tích hợp; POST một chạm kiểm đơn vị phần phân tích, đi qua cùng lớp chống SSRF của proxy ảnh | ✅ |
| P8-08 | Danh bạ | Tạo, sửa, ảnh (chỉ JPEG/PNG ≤ 1 MB, kiểm bằng byte đầu), nhóm, xoá | Như mong đợi | Như mong đợi | ✅ |
| P8-09 | vCard / CSV | Nhập vCard 4.0/3.0/2.1 (QP), CSV Google và Outlook; xuất rồi nhập lại | Đọc đúng tên, nhiều thư/số, tổ chức, ghi chú, ảnh; dòng gấp ≤ 75 octet; CSV chặn công thức bảng tính | Như mong đợi | ✅ |
| P8-10 | Gộp trùng | Nhập thẻ trùng địa chỉ; tạo tay trùng rồi "Gộp" | Gộp vào liên hệ có sẵn; còn một liên hệ | Như mong đợi | ✅ |
| P8-11 | Liên hệ khác | Gửi 2 thư tới một người | Một liên hệ tự lưu, đếm 2 (không đếm lại khi bộ điều phối chạy lại) | Như mong đợi | ✅ |
| P8-12 | Danh bạ tổ chức | Tìm; quản trị viên ẩn một người | Người dùng, nhóm, hộp thư dùng chung; người bị ẩn biến mất (cả ở gợi ý người nhận) | Như mong đợi | ✅ |
| P8-13 | Cài đặt | Lưu giá trị sai; "luôn hiện ảnh"; hồ sơ (ngôn ngữ, múi giờ) | Về mặc định; ảnh hiện (trừ thư rác); ngôn ngữ lạ bị từ chối | Như mong đợi | ✅ |
| P8-14 | Phím tắt | Bật trong Cài đặt; `?`, `g i`, `j`, `x`, `z` | Bảng 35 phím; điều hướng; con trỏ thấy được; hoàn tác thao tác vừa làm | Như mong đợi (trình duyệt, `shortcuts.test.ts`) sau BUG-043 | ✅ |
| P8-15 | Dọn Thùng rác/Thư rác | Thư trong Thùng rác > 30 ngày, một thư bị giữ pháp lý, một thư mới | Xoá hẳn thư cũ; giữ thư bị giữ và thư mới | Như mong đợi | ✅ |

Chưa làm trong Phase 8 (đúng phase sau): giao diện quản trị cho quy tắc phân loại và ẩn khỏi danh bạ tổ chức (Phase 9, API đã có);
mật khẩu ứng dụng và hướng dẫn IMAP trong tab "Chuyển tiếp và IMAP" (Phase 11).

## Phase 9 — bảng điều khiển quản trị

Chạy: `dotnet test tests/BlueMail.UnitTests --filter Admin`, `dotnet test tests/BlueMail.IntegrationTests --filter AdminConsoleTests`
(DNS giả `FakeDomainDns` trong StackFixture), web `npx vitest run`; trình duyệt (desktop và 375 px) với dev stack và CoreDNS thật.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P9-01 | Thêm tên miền | Thêm `khach-x.test`; thêm lại chữ hoa | Hiện bản ghi cần tạo kèm nút sao chép; 409 | Như mong đợi | ✅ |
| P9-02 | Xác minh | Chưa có TXT: gửi thư tới tên miền; tạo TXT, MX, SPF, DKIM; Kiểm tra DNS | Không nhận; đã xác minh, MX/SPF/DKIM "Đúng", DMARC "Chưa có" | Như mong đợi | ✅ |
| P9-03 | Đổi khoá DKIM | Đổi; đổi lần hai; tạo bản ghi khoá mới; kiểm | PENDING; 409; khoá mới ACTIVE, khoá cũ RETIRING 7 ngày | Như mong đợi (sau BUG-047/048) | ✅ |
| P9-04 | MTA-STS | GET `/.well-known/mta-sts.txt` với Host `mta-sts.<tên miền>` | Chính sách STSv1 với `mx:` của nền tảng | Như mong đợi | ✅ |
| P9-05 | Tên miền bí danh, nhận thư lạc | Bí danh của tên miền khách; catch-all ra ngoài; catch-all nội bộ; xoá tên miền đang dùng | Thư tới bí danh vào đúng hộp; 400; thư lạc vào hộp đã chọn; 409 | Như mong đợi | ✅ |
| P9-06 | Kiểm DNS thật | Trình duyệt, bluemail.test với CoreDNS dev | Mọi bản ghi bắt buộc "Đúng" | Như mong đợi sau BUG-049 | ✅ |
| P9-07 | Nhập CSV | Tệp có địa chỉ sai, trùng, tên miền lạ; sửa rồi gửi lại | Không tạo gì, lỗi theo dòng; tạo, hiện mật khẩu tạm một lần | Như mong đợi | ✅ |
| P9-08 | Thao tác người dùng | Đặt lại mật khẩu, thêm bí danh (trùng → 409), đổi địa chỉ, xoá kèm chuyển thư | Mật khẩu 16 ký tự; địa chỉ cũ vẫn nhận thư; thư chuyển sang người nhận | Như mong đợi | ✅ |
| P9-09 | Nhóm | Chỉ thành viên gửi; thành viên ngoài; ANYONE + kiểm duyệt; nhóm nội bộ nhận thư người ngoài qua MX | Từ chối người lạ, cho thành viên ngoài; người lạ vào kiểm duyệt, quản lý bỏ qua; 550 5.7.1 | Như mong đợi (`mx_checks.py`) | ✅ |
| P9-10 | Hộp thư dùng chung | Tạo, cấp Đọc + Gửi với tư cách | Địa chỉ xuất hiện trong "Từ" của người được cấp | Như mong đợi | ✅ |
| P9-11 | Chính sách | Giá trị sai kiểu; khoá lạ; chặn `.exe`; tắt cảnh báo người gửi ngoài | 400; 404; tải lên bị từ chối; `/me` báo tắt, dải cảnh báo không hiện | Như mong đợi | ✅ |
| P9-12 | Định tuyến | Quy tắc thư đi REDIRECT; thư đến BCC theo người nhận | 400; hành động BCC khi khớp | Như mong đợi | ✅ |
| P9-13 | Truy vết, hàng đợi, danh sách ngừng gửi | Tìm theo người gửi; chi tiết; gỡ địa chỉ ngừng gửi; thử lại/huỷ | Một dòng có điểm rác; bản ghi biến mất; trạng thái đổi, có nhật ký | Như mong đợi | ✅ |
| P9-14 | Báo cáo | Tỉ lệ rác; loại lạ; XLSX người chưa bật 2 bước; In / PDF | Có cột `spam_percent`; 400; tệp mở được, có dòng bộ lọc và "Đầy đủ"; bản in không có điều hướng | Như mong đợi | ✅ |
| P9-15 | Tổ chức (nền tảng) | Quản trị tổ chức xem tổ chức; nền tảng xem mức dùng; đổi chế độ tạm ngưng | 403; số người dùng; `suspendTemporary` lưu | Như mong đợi | ✅ |
| P9-16 | Đăng nhập hộ | Lý do rỗng; có lý do | 400; liên kết một lần, nhật ký kiểm toán, thư báo quản trị tổ chức, dải cảnh báo trong phiên | Như mong đợi | ✅ |
| P9-17 | Giao diện | Mọi thẻ, 375 px, bảng điều khiển console | Không lỗi console, bảng cuộn ngang, ngăn chi tiết vừa màn hình | Như mong đợi | ✅ |

## Phase 10 — tuân thủ, nhập/xuất dữ liệu, máy khách IMAP

Chạy: `dotnet test tests/BlueMail.UnitTests --filter "Imap|Imports"`, `dotnet test tests/BlueMail.InteropTests --filter "ImapClient|Mbox"`,
`dotnet test tests/BlueMail.IntegrationTests --filter ComplianceMigrationTests`, fuzz `imap`. Nguồn IMAP thử nghiệm: Dovecot của
`doitac.test` (127.0.0.1:55993, chứng chỉ dev ghim), nạp dữ liệu bằng `tests/interop/imap_source.py populate`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P10-01 | Bộ phân tích IMAP | Ví dụ RFC 3501, literal lớn (ra đĩa), Gmail X-GM-LABELS, đầu vào hỏng, fuzz | Đúng giá trị; lỗi ồn ào; không treo/không nổ bộ nhớ | Như mong đợi | ✅ |
| P10-02 | Máy khách IMAP vs imaplib | Dovecot thật, thư mục tên Việt lồng nhau, thư 4,8 MB | Cùng thư mục, UIDVALIDITY, UID, cờ, ngày, kích thước, SHA-256 | Như mong đợi | ✅ |
| P10-03 | Bảo mật kết nối | Sai mật khẩu; chứng chỉ tự ký không ghim; cổng 25 | AUTHENTICATIONFAILED; TLS từ chối; 400 `import_port_not_allowed` | Như mong đợi | ✅ |
| P10-04 | Nhập IMAP | Kiểm tra → nhập → chạy lại | 8 thư, giữ cờ/ngày/nhãn lồng nhau, thư `\Deleted` bỏ qua; lần hai 0 thư mới; không có thư đi | Như mong đợi (cả qua trình duyệt) | ✅ |
| P10-05 | Nhập mbox | Takeout (X-Gmail-Labels, "Unread"), mutt (Status/X-Status), dòng "From " trong thân | Nhãn và cờ đúng; trích dẫn mboxrd gỡ đúng một lần | Như mong đợi sau BUG-052 | ✅ |
| P10-06 | mbox với công cụ ngoài | BlueMail ghi → `mailbox` Python đọc; Python ghi → BlueMail đọc | Cùng số thư, cùng byte | Như mong đợi | ✅ |
| P10-07 | Giữ pháp lý | Giữ theo người; thư đến sau; người dùng xoá vĩnh viễn; tìm eDiscovery; gỡ giữ; dọn | Thư cũ và mới đều bị giữ; ẩn với người dùng nhưng còn; tìm thấy, có nhật ký; xoá hẳn sau khi gỡ | Như mong đợi | ✅ |
| P10-08 | Giữ theo truy vấn | Điều kiện khớp 1 thư; điều kiện sai cú pháp | Matched = 1, thư bị giữ; 400 | Như mong đợi | ✅ |
| P10-09 | Lưu giữ | KEEP_MIN 30 ngày: xoá thư mới / thư 40 ngày; DELETE_AFTER 365 ngày theo điều kiện | Thư mới chỉ ẩn, thư cũ xoá hẳn; chỉ thư quá hạn khớp điều kiện bị xoá | Như mong đợi | ✅ |
| P10-10 | eDiscovery xuất | Tìm trên hai hộp thư, xuất mbox | Mỗi hộp thư một mbox + manifest.csv; băm khớp; liên kết chỉ cho người yêu cầu; SHA-256 tệp khớp | Như mong đợi | ✅ |
| P10-11 | Tải dữ liệu của tôi | Yêu cầu; yêu cầu lần hai | ZIP `mail/INBOX.mbox`, `contacts.vcf`, `settings.json`; 409 | Như mong đợi | ✅ |
| P10-12 | Nhập thật từ Gmail và Outlook.com | Hộp thư thử nghiệm thật, mật khẩu ứng dụng | Đủ thư, nhãn Gmail; chạy lại không trùng | **Chưa chạy**: cần tài khoản thử nghiệm của chủ sản phẩm (mã đã đối chứng với Dovecot; ánh xạ X-GM-LABELS kiểm đơn vị) | ⏳ |

## Phase 11 — SMTP nộp thư và IMAP

Chuẩn bị một lần: tạo người dùng `imaptest@bluemail.test` / `Imaptest-Dev-Password-1` (trang Quản trị → Người dùng) và tải kịch bản
`imaptest` (mã nguồn công khai của Dovecot) vào thư mục đầu ra (không đưa vào git):
`curl -L https://github.com/dovecot/imaptest/archive/refs/heads/main.tar.gz | tar xz -C tests/interop/out/imaptest`.
Chạy: `sh scripts/dev-phase11-check.sh` (Python imaplib/smtplib + imaptest), `dotnet test --filter "SmtpAuth|ImapServerUnit"`,
`dotnet test tests/BlueMail.IntegrationTests --filter MailProtocolTests`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P11-01 | IMAP tuân thủ | `imaptest` 74 nhóm kịch bản | Không lỗi ở giao thức cơ bản | 0/460 lệnh cơ bản sai; 1/60 lệnh mở rộng (đăng ký thư mục không tồn tại, `\NonExistent`); 38 nhóm bỏ qua vì mở rộng không cam kết (SORT, THREAD, ESEARCH, BINARY, CATENATE, NOTIFY…) | ✅ |
| P11-02 | IMAP đồng thời | `imaptest` stress 5 client × 40 giây | Không báo lỗi nhất quán | Không lỗi | ✅ |
| P11-03 | TLS và đăng nhập | 143 trước STARTTLS; sai mật khẩu; mật khẩu ứng dụng; 2FA bật | LOGINDISABLED/PRIVACYREQUIRED; AUTHENTICATIONFAILED; được; chỉ mật khẩu ứng dụng | Như mong đợi | ✅ |
| P11-04 | Dò mật khẩu | 10 lần sai | Khoá tạm, kể cả mật khẩu đúng | Như mong đợi | ✅ |
| P11-05 | Thu hồi | Thu hồi mật khẩu ứng dụng khi IMAP đang mở | Kết nối bị đóng ngay | Như mong đợi (`MailProtocolTests`) | ✅ |
| P11-06 | IDLE, QRESYNC | Kết nối khác thêm thư; xoá thư rồi SELECT QRESYNC | EXISTS tới ngay; VANISHED (EARLIER) | Như mong đợi | ✅ |
| P11-07 | SMTP nộp thư | 587 STARTTLS + AUTH; 465; From không được phép; người nhận nội bộ không tồn tại | AUTH chỉ sau TLS; 250; 550 5.7.1; 550 5.1.1 ở RCPT | Như mong đợi | ✅ |
| P11-08 | Đã gửi không trùng | Gửi qua SMTP rồi APPEND vào Sent (như Thunderbird) | Một bản | Như mong đợi | ✅ |
| P11-09 | Ra ngoài | Thư nộp qua SMTP tới Postfix doitac.test | Tới nơi, `dkim=pass` ở bên nhận | Như mong đợi | ✅ |
| P11-10 | Tự cấu hình | autoconfig, autodiscover | IMAP 993 / SMTP 465 SSL | Như mong đợi | ✅ |
| P11-11 | Phần mềm thư thật | Thunderbird, Outlook, iPhone Mail đọc/gửi/xoá/gắn nhãn hai chiều với web | Đồng bộ hai chiều | **Outlook 16.0.20625 (Windows), 2026-11-24**, hồ sơ riêng, IMAP 993 + SMTP 465 SSL/TLS, chứng chỉ dev: Outlook→máy chủ đánh dấu đọc, gắn cờ (→ STARRED), xoá (→ TRASH) đúng; gửi tới lan@ → lan nhận, đúng **một** bản Sent (sau sửa BUG-080); web gắn sao → Outlook hiện cờ sau F9. Tìm và sửa trong lúc chạy: BUG-078 (Garage mất khối khi Docker tắt cứng; IMAP cắt phiên khi đọc S3 lỗi; FETCH dài bị Outlook coi là treo), BUG-079 (IDLE lần hai), BUG-080 (Outlook APPEND bản Sent với Message-ID mới). Còn lại: các thư dev nhận sau 16:14 UTC 23/11 mất nội dung (dữ liệu dev, trước khi bật fsync) nên Outlook báo `800CCCD2` (máy chủ trả NO có lý do) cho các thư đó — đúng thiết kế. **Thunderbird 160.0 (Windows), 2026-11-24**, hồ sơ riêng cấu hình bằng `user.js`, lan@bluemail.test: thư mục khớp máy chủ (Inbox 90 chưa đọc, nhãn lồng `Dự án/Q4`, tên tiếng Việt), Unicode/IDN hiển thị đúng; Thunderbird→máy chủ gắn sao, đã đọc, xoá (→ TRASH) đúng; gửi tới minh@ → minh nhận, đúng một bản Sent; thay đổi phía máy chủ (cờ đặt từ một phiên IMAP khác) hiện trong Thunderbird sau ≤ 15 s không cần làm mới (IDLE). Điều khiển GUI bằng agent-desktop. iPhone Mail chưa chạy (cần thiết bị iOS) | ⚠️ Outlook ✅, Thunderbird ✅, iPhone ⏳ |

## Phase 12 — Gửi thư quy mô thật, DNSSEC/DANE, chế độ bảo mật

Chuẩn bị: vùng `doitac.test` được CoreDNS ký (`deploy/coredns/keys`, tạo bằng `scripts/dev-gen-dnssec.py`), outbound tin
nó qua `BM_DNSSEC_ANCHORS`; chạy lại `python scripts/dev-gen-zones.py` + `docker restart bluemail-dev-dns-1` khi đổi
vùng. Chạy: `sh scripts/dev-phase12-check.sh`; `dotnet test tests/BlueMail.InteropTests --filter Dnssec` (cần Internet
cho phần thật; `BM_OFFLINE=1` để bỏ qua); `dotnet test tests/BlueMail.IntegrationTests --filter DeliverabilityTests`.

| Mã | Chức năng | Bước | Kết quả mong đợi | Kết quả thực tế | Đạt |
|---|---|---|---|---|---|
| P12-01 | DNSSEC khớp dnspython | 16 tên thật (ietf.org, isc.org, cloudflare.com, nic.cz, example.com, google.com, dnssec-failed.org…) | Cùng Secure/Insecure/Bogus | Khớp 16/16 (3 lần liên tiếp) | ✅ |
| P12-02 | DNSSEC vùng dev | CoreDNS ký doitac.test; tên có, không có, kiểu không có; neo sai | Secure; neo sai → Bogus | Như mong đợi | ✅ |
| P12-03 | DANE gửi thành công | Gửi tới an@doitac.test (TLSA 3 1 1 khớp khoá Postfix) | TLS bắt buộc, giao thành công, `tls_results` = tlsa thành công | Như mong đợi | ✅ |
| P12-04 | DANE chặn | Gửi tới dane-bad.doitac.test (TLSA không khớp) | Không gửi văn bản trần; DEFERRED; `tlsa-invalid` | Như mong đợi | ✅ |
| P12-05 | VERP | Xem `MAIL FROM` ở Postfix bên nhận | `bounces+<id>.<mac>@bluemail.test` | Như mong đợi | ✅ |
| P12-06 | Trả lại muộn | DSN từ null sender tới địa chỉ VERP | Người nhận BOUNCED, chặn gửi, sự kiện ghi một lần, thư báo vào Hộp thư đến người gửi | Như mong đợi | ✅ |
| P12-07 | ARF | Báo cáo `abuse` tới fbl@bluemail.test | Sự kiện COMPLAINT, địa chỉ bị chặn gửi | Như mong đợi | ✅ |
| P12-08 | Tự khoá | 25 người nhận, 4 trả lại cứng trong 24 giờ | Khoá; gửi bị từ chối `sending_locked`; quản trị mở được | Như mong đợi | ✅ |
| P12-09 | Làm ấm | IP duy nhất của dải đã chạm trần hôm nay | Thư chờ, không tính lần thử, thử lại sau 30 phút | Như mong đợi | ✅ |
| P12-10 | TLS-RPT | Kết quả hôm qua của doitac.test | Báo cáo gzip JSON ký DKIM tới `rua`, đúng RFC 8460 (tên tệp, tiêu đề, trường) | Như mong đợi | ✅ |
| P12-11 | Chế độ bảo mật | Gửi bảo mật có mã; mở bằng mã sai/đúng; gửi lại; thu hồi | Bên ngoài chỉ nhận liên kết; mã sai 403; gửi lại không trùng; thu hồi → 403; người khác không thu hồi được | Như mong đợi | ✅ |
| P12-12 | IP thật, Gmail/Outlook thật | Gửi từ IP công khai có PTR tới Gmail, Outlook.com; đăng ký Postmaster/SNDS/JMRP | Vào Hộp thư đến, `dkim=pass spf=pass dmarc=pass` | **Chưa chạy được.** Khảo sát 2026-11-24 trên mạng dev: cổng 25 ra ngoài **mở** (EHLO tới gmail-smtp-in.l.google.com và outlook-com.olc.protection.outlook.com đều 250); IP công khai 14.169.234.77 (VNPT, động) có PTR chung `static.vnpt.vn` không trỏ ngược về IP — Gmail yêu cầu PTR khớp (FCrDNS) với IPv4. Điều kiện để chạy: một tên miền thật do chủ sản phẩm quản lý (MX/SPF/DKIM/DMARC theo docs/04) + IP có PTR riêng (VPS, hoặc phiếu hỗ trợ VNPT) — xem docs/00 Z6 Khảo sát VM BlueStar 14.225.83.93 (2026-11-24, người dùng chọn dùng VM này): cổng 25 ra Gmail/Outlook/Yandex **mở**; PTR hiện là `static.vnpt.vn` (không khớp xuôi-ngược) → Gmail/Outlook sẽ từ chối hoặc đưa vào spam, nên chưa gửi. bluestar.com.vn dùng Microsoft 365 (MX, SPF ở tên miền gốc) → không đụng; dùng tên miền con `bm.bluestar.com.vn`. Việc người dùng cần làm: (1) yêu cầu VNPT đặt PTR 14.225.83.93 → `mail.bm.bluestar.com.vn`; (2) ở PA Vietnam thêm A `mail.bm` → 14.225.83.93, MX `bm` → `mail.bm.bluestar.com.vn`, TXT `bm` `v=spf1 ip4:14.225.83.93 -all`, TXT `_dmarc.bm` `v=DMARC1; p=none; rua=mailto:dmarc@bm.bluestar.com.vn`; bản ghi DKIM lấy từ trang quản trị tên miền BlueMail sau khi triển khai. Sau đó: triển khai BlueMail lên VM (ảnh build trên GitHub Actions, không build trên VM), gửi thử tới Gmail/Outlook.com, đăng ký Postmaster/SNDS/JMRP. Đã chuẩn bị (2026-11-24): image build trên GitHub Actions (`images.yml`, lượt 45285560758 xanh), VM kéo được; overlay `deploy/vm/docker-compose.vm.yml` và site `deploy/vm/bluemail.caddy` (docs/04 "Máy chủ dùng chung"). Chỉ còn chờ PTR + DNS. **Triển khai trên VM 2026-11-24 (ảnh 8053dc9):** 11 dịch vụ healthy, ~820 MB RAM; A `mail.bm` đã có, Caddy chung cấp chứng chỉ Let's Encrypt cho `mail.bm.bluestar.com.vn` (HTTPS 200), submission/imap tạm dùng chứng chỉ này. Từ Internet: 443 vào được, **25 và 993 không vào được** (tường lửa nhà cung cấp VM; cần mở để nhận thư trả về/báo cáo DMARC, không cần để gửi). Còn thiếu: PTR (VNPT vẫn trả `static.vnpt.vn` ở cả máy chủ gốc), A `bm`/`id.bm`/`smtp.bm`/`imap.bm`, MX/SPF/DMARC; khoá DKIM sinh ở lần gửi đầu, khi đó công bố TXT `<selector>._domainkey.bm`. **Cập nhật:** người dùng đã thêm A `bm`/`id.bm`/`smtp.bm`/`imap.bm`; Caddy chung cấp đủ 5 chứng chỉ; web và BlueID chạy (HTTPS 200); đã đổi mật khẩu quản trị, tạo `test@bm.bluestar.com.vn`, sinh khoá DKIM `bm202611232327`. Còn chờ: TXT xác minh, MX, SPF (`include:_spf.bm` + `_spf.bm` `ip4:14.225.83.93`), DKIM, DMARC, TLS-RPT (bảng giá trị trong trang quản trị tên miền) và **PTR từ VNPT**. **Cập nhật 2:** tên miền đã xác minh (TXT, MX, SPF, DMARC, TLS-RPT đạt; resolver của VM giữ kết quả âm cũ nên đặt `BM_DNS_SERVERS=1.1.1.1,8.8.8.8`). DKIM chưa đạt: PA Vietnam cắt TXT còn ~230 ký tự, cần nhập thành hai chuỗi. PTR vẫn `static.vnpt.vn`. **Cập nhật 3:** DKIM đạt sau khi nhập thành hai chuỗi (PA Vietnam để lại một dấu cách trong `p=`, hợp lệ theo RFC 6376 §3.6.1; bộ kiểm tra bỏ khoảng trắng); khoá `bm202611232327` ACTIVE. Cache DNS trong tiến trình giữ bản cắt cũ tới hết TTL; khởi động lại api xoá được. Chỉ còn thiếu PTR. **Chủ sản phẩm quyết định bỏ qua (2026-11-24)**: PTR do nhà cung cấp IP đặt; làm lại khi có PTR. | ⏭️ |

## Phase 13 — Hiệu năng, truy cập, PWA, bản chạy thật, sao lưu, fuzz dài

Máy đo: laptop Windows 11, 32 GB, Docker Desktop (WSL2), PostgreSQL 16 trong container, API chạy Debug ngoài container —
chậm hơn máy chủ thật; số dưới đây là cận trên. Dữ liệu đo: `tests/perf/fill.sql` (thư tổng hợp đúng dạng kho thư, dọn
bằng `cleanup.sql`); đo qua HTTP thật: `tests/perf/api_latency.py` (hoặc `web-latency.js` trong trình duyệt).

| Mã | Chỉ tiêu (PROMPT 8.2) | Cách đo | Kết quả | Đạt |
|---|---|---|---|---|
| P13-01 | Mở Hộp thư đến (50 hội thoại) < 400 ms p95, hộp thư 100.000 thư | 100 lần | Trước sửa: p95 392 ms; sau (D30): **183 ms**; với 1.000.000 thư (600.078 trong Hộp thư đến): **99 ms** | ✅ |
| P13-02 | Tìm kiếm < 1 s p95, hộp thư 1.000.000 thư | 100 lần, 10 truy vấn (từ, cụm từ, from:, subject:, has:, khoảng ngày, is:, không có kết quả) | Trước: p95 ≈ 3,0–3,9 s; sau: **p95 502 ms**, chậm nhất (cụm từ không ai viết) 560 ms | ✅ |
| P13-03 | Nhận ≥ 50 thư/s duy trì, `250` < 1 s p95 (< 1 MB) | `tests/perf/smtp_load.py`, thư 20 KB, 16 kết nối | **60 thư/s trong 120 s**: 7.200 thư, p95 93 ms, 0 lỗi; thử 150/s: đạt 112 thư/s, p95 183 ms, 0 lỗi | ✅ |
| P13-04 | 2.000 kết nối IMAP IDLE | `tests/perf/imap_idle_load.py` 2.000 phiên TLS, LOGIN, SELECT, IDLE; một thư mới | Trước: MX `451` (BUG-069); sau: 2.000/2.000 nhận `EXISTS`, p50 1,27 s, p95 1,63 s, 0 lỗi; mở 2.000 phiên mất 97 s | ✅ |
| P13-05 | 500 người dùng web đồng thời | `tests/perf/web_load.py`: 500 người dùng ảo, mỗi người một kết nối, lặp lại mở Hộp thư đến → mở hội thoại → đọc thân thư, 25 % tìm kiếm, 10 % tải nhãn, nghỉ 1–3 s mỗi bước; API cho phép tốc độ đó từ một địa chỉ (`BM_API_RATE_PER_MINUTE`) | Hộp thư 10.000 thư, 5 phút: **344 yêu cầu/s, p95 301 ms** (Hộp thư đến 266 ms, hội thoại 216 ms, thân thư 424 ms, tìm kiếm 422 ms), **0 lỗi / 105.544 yêu cầu**. Thử quá tải: 500 người dồn vào **một** hộp thư 100.000 thư trên laptop: trước sửa 142 yêu cầu/s và 14.670 lỗi (đếm nhãn 1,2 s, pool CSDL cạn, BUG-076); sau sửa 176 yêu cầu/s, vẫn lỗi lúc 500 người vào cùng lúc — vượt sức CSDL của laptop, cần máy chủ CSDL cỡ thật | ✅ |
| P13-06 | Truy cập | Lighthouse (snapshot) | Hộp thư đến 90 → **100**, Cài đặt 91 → **100**, Quản trị **100**, trang đăng nhập BlueID **100** | ✅ |
| P13-07 | PWA | `manifest.webmanifest`, `sw.js`, biểu tượng 192/512/maskable; bản build | Cài được; khung mở khi ngoại tuyến; không cache dữ liệu (D32); gói đầu 2,9 MB → 510 kB | ✅ |
| P13-08 | Điện thoại (375 px) | Ảnh chụp: hộp thư, soạn, chế độ bảo mật, trang xem thư bảo mật, thư đã gửi | Hiển thị đúng | ✅ |
| P13-09 | `docker compose up -d` lên hệ thống đầy đủ có dữ liệu mẫu | `deploy/docker-compose.yml` (13 dịch vụ) với `BM_SEED_SAMPLE_DATA=true`, cổng thử 8443/2525/2993… | Mọi dịch vụ `healthy`; web, OIDC trả 200; 4 hộp thư nhận 12 thư mẫu **qua SMTP** (đa ngôn ngữ, RTL, tệp tên tiếng Việt, bản tin List-Unsubscribe, lời mời lịch, thư giả dạng, thư rác) | ✅ |
| P13-10 | Sao lưu / phục hồi thử thật | `backup.sh`, `down -v`, `restore.sh` | docs/03 mục 4: 16 s sao lưu, 49 s phục hồi, nội dung IMAP trùng SHA-256 | ✅ |
| P13-11 | Fuzz dài | Bộ fuzz đột biến 4 phút/đích × 8 đích (SMTP, IMAP, MIME, tiêu đề, DNS kể cả DNSSEC, DKIM, bộ quét, văn bản), 2 lượt hạt giống khác nhau | Lượt 1 (4 phút/đích): 3 lỗi (BUG-066/067/068); lượt 2 (4 phút/đích, hạt giống khác): 1 lỗi (BUG-073); tất cả đã sửa, có phép thử hồi quy; lượt 3 (3 phút/đích, hạt giống 20261125): **không lỗi**. libFuzzer (SharpFuzz) cho chạy nhiều giờ vẫn cần container Linux (Program.cs) | ✅ |
| P13-12 | Dữ liệu mẫu chặn thư rác | Thư "trúng thưởng" mẫu | Trước: 2,3 điểm (vào Hộp thư đến); thêm luật `ADVANCE_FEE`: vào Thư rác; bộ thư mẫu: bắt nhầm 0/495 | ✅ |
| P13-13 | Gợi ý khi gõ ở ô tìm kiếm (PROMPT 6.7) | Gõ "test" trên bản chạy ở VM | Gợi ý người liên hệ, nhãn, tìm gần đây; không dấu | Hiện "BlueMail Test <test@bm.bluestar.com.vn>"; chọn → `from:test@bm.bluestar.com.vn`; ô trống hiện lần tìm gần đây; ảnh chụp 375 px không tràn; `search.test.ts` 4 ca | ✅ |
| P13-14 | Báo cáo DMARC/TLS-RPT nhận về (PROMPT 7.6) | Gửi báo cáo DMARC (gzip) qua SMTP tới `dmarc-reports@bluemail.test` ở MX dev; test tích hợp với báo cáo TLS-RPT | Lưu theo tổ chức sở hữu tên miền, một lần, không cất làm thư; hiện ở Báo cáo | MX trả 250 "Report received", lưu 1 báo cáo 7 thư DKIM pass; gửi lại không trùng; tên miền lạ bị bỏ; quản trị tổ chức khác không thấy; XXE và bom nén bị chặn (`AggregateReportParserTests` 6 ca) | ✅ |
| P13-15 | Thao tác hàng loạt > 500 hội thoại (PROMPT 6.3, 8.3) | "Chọn tất cả N" rồi gắn sao / đánh dấu đã đọc trên một tìm kiếm | Chạy nền theo lô 500, có tiến độ, có trần (50.000 hội thoại; 3 việc/người) | `RulesTests.Select_all_of_a_search…`: QUEUED → DONE 3/3, người khác không xem được việc (404), việc thứ 4 bị từ chối `too_many_jobs`; thử tay trên dev (minh@, `subject:Load`, 3.475 hội thoại): "Chọn tất cả 3.475" → đánh dấu đã đọc xong trong ~10 s, chưa đọc 3.526 → 53; rồi đánh dấu chưa đọc: tiến độ "1.000/3.475… 2.000/3.475… Đã xử lý 3.475 hội thoại" | ✅ |

## Giống Outlook trên web (yêu cầu thêm của chủ sản phẩm, 2026-11-24)

| Mã | Kịch bản | Cách chạy | Kết quả mong đợi | Kết quả | Trạng thái |
|---|---|---|---|---|---|
| OL-01 | Bố cục thư | Mở Hộp thư đến ở 1600 px và 375 px | Ngăn đọc bên phải mặc định; danh sách dạng thẻ 3 dòng có ảnh đại diện chữ cái đầu; tiêu đề nhóm theo ngày (múi giờ, ngày đầu tuần của người dùng); bộ lọc nhanh Tất cả/Chưa đọc/Gắn sao/Có tệp; mục Yêu thích ghim được; thanh ứng dụng bên trái (ẩn trên điện thoại) | Như mong đợi (ảnh chụp 1600 px và 375 px, không tràn ngang); `mailView.test.ts` (nhóm ngày, chữ cái đầu, màu) | ✅ |
| OL-02 | Chuyển tiếp dưới dạng tệp đính kèm | Menu "…" của một thư → Chuyển tiếp dưới dạng tệp đính kèm; gửi | Người nhận thấy đúng một tệp `<tiêu đề>.eml` (`message/rfc822`, 7bit/8bit theo RFC 2046 5.2.1; thư gốc không hợp lệ 8bit thì gửi `.eml` base64); thân và tệp bên trong thư gốc không lẫn vào thư ngoài; không đính kèm được thư của hộp thư khác | `SendingTests.Forward_as_attachment…`, `BuilderTests` (mã hoá, tệp lồng) | ✅ |
| OL-03 | Dọn sạch (Sweep) | Thanh công cụ thư trong Hộp thư đến → Dọn sạch | Chuyển mọi hội thoại từ người gửi (hoặc chỉ cái cũ hơn 10 ngày) vào Thùng rác bằng tác vụ nền, tuỳ chọn chặn người gửi | `Sweep.test.ts`; tác vụ nền dùng lại P13-15 | ✅ |
| OL-04 | Thẻ lời mời họp (PROMPT 6.3) | Mở "Lời mời: Họp tiến độ dự án" (tệp invite.ics) trên web; bấm Có; test tích hợp với lời mời kiểu Outlook (text/calendar; method=REQUEST) | Thẻ: tiêu đề, thời gian theo múi giờ người đọc, người tổ chức, khách mời, Có/Có thể/Không; trả lời gửi REPLY iMIP tới người tổ chức, bản sao trong Đã gửi; đổi ý giữ một bản ghi; người tổ chức thấy "đã chấp nhận" | Như mong đợi (ảnh chụp 1600 px); `ICalendarTests` 18, `InviteCard.test.ts` 3 | ✅ |
| OL-05 | Thao tác trên từng thư + chi tiết người gửi (PROMPT 6.3) | Menu "…" của một thư: Xoá thư này, Đánh dấu chưa đọc từ đây; nút ▾ cạnh dòng "tới" | Chỉ thư đó vào Thùng rác, phần còn lại của hội thoại giữ nguyên; chưa đọc từ thư đó trở xuống; bảng Từ/Tới/Cc/Ngày/Tiêu đề/Gửi qua (SPF đạt)/Được ký bởi (DKIM đạt)/Bảo mật (TLS tại MX) | `SendingTests.Reply_reply_all…` (xoá một thư), `mailView.test.ts` (chi tiết) | ✅ |
| OL-06 | Cảnh báo đăng nhập mới, quên mật khẩu (PROMPT 13) | Đăng nhập web bằng trình duyệt A hai lần rồi B; trang `/account/forgot-password` với địa chỉ có thật, lặp lại, và địa chỉ không có | Chỉ B (trình duyệt và IP chưa thấy trong 180 ngày) gửi thư "Đăng nhập mới" vào hộp thư chủ tài khoản; lần đăng nhập đầu và đăng nhập sai không gửi; quên mật khẩu báo quản trị viên tổ chức một lần mỗi giờ, câu trả lời giống hệt cho địa chỉ không có, thời gian trả lời ≥ 800 ms; giới hạn 5 lần/15 phút mỗi IP | `AccountNoticeTests` | ✅ |
| OL-07 | Lịch (Ngày/Tuần/Tháng) | Trang `/calendar` ở 1600 px và 375 px; bấm ô 10:00 thứ Năm → tạo "Họp giao ban phòng Kỹ thuật", hằng tuần, mời lan@; test tích hợp hai người dùng | Sự kiện hiện đúng giờ ở Tuần và lặp ở Tháng; vạch giờ hiện tại; điện thoại mặc định Ngày, không tràn ngang; khách nhận thư mời, trả lời Có → vào lịch khách, người tổ chức thấy "Tham dự"; bỏ một lần (EXDATE), huỷ cả chuỗi → khách nhận thư huỷ, sự kiện biến khỏi lịch khách | Như mong đợi; `CalendarTests` 2, `RecurrenceTests` 14 (gồm ví dụ RFC 5545), `ICalendarTests` (VTIMEZONE), `calendarView.test.ts` 5 | ✅ |
| OL-08 | Việc cần làm (To Do) | Trang `/tasks`: thêm 2 việc bằng Enter, gắn sao một việc; menu "…" của thư → Thêm vào Việc cần làm; test tích hợp | Việc quan trọng lên đầu; danh sách Quan trọng/Có hạn/Đã xong đúng; xong ↔ chưa xong; việc từ thư lấy tiêu đề thư và mở lại được hội thoại; người khác không xem/sửa/xoá được, không tạo việc từ thư của người khác | Như mong đợi (ảnh chụp 1600 px); `TaskTests`, `tasksApi.test.ts` | ✅ |
| OL-09 | Nhắc lịch, Tuần làm việc, Đến ngày, thư gắn cờ trong Việc cần làm | Tạo sự kiện đang diễn ra (nhắc trước 10 phút) ở `/calendar`; chọn Tuần làm việc; Việc cần làm → Thư gắn cờ | Thông báo hiện trong ≤ 1 phút (một lần mỗi sự kiện), kèm thông báo máy tính nếu đã cho phép; Tuần làm việc T2–T6; thư gắn sao hiện như việc, đánh dấu xong thì bỏ sao | Như mong đợi; `calendarView.test.ts` (nhắc, tuần làm việc) | ✅ |
| OL-10 | Hộp thư dùng chung trên web, gửi thay mặt (PROMPT 6.2, 6.4, 7.3) | Quản trị viên tạo support-…@acme.test, cấp tomas đọc + gửi thay mặt; tomas chọn hộp thư ở đầu cột thư mục và gửi từ support | Hộp thư dùng chung hiện trong danh sách và mở được (nhãn, thư); mọi lệnh thư mang `mailboxId`; chọn "Thay mặt Support" thì thư đi với From = support, Sender = tomas (RFC 5322 3.6.2); người không được cấp không mở được | `SendingTests.Shared_mailbox_is_listed…`, `mailbox.test.ts` | ✅ |
| E2E-01 | Playwright đầu-cuối (PROMPT 11) | `cd tests/e2e && npm install && npx playwright test` trên dev stack đang chạy; Chrome cài sẵn; 1366×768 và 375×812 | Đăng nhập, đọc hội thoại; soạn có tệp → gửi → hoàn tác → gửi lại → có trong Đã gửi; tìm `from:` → tạo bộ lọc từ tìm kiếm → tạm ẩn; quản trị thêm tên miền (bảng bản ghi DNS) và người dùng; Lịch, Việc, Danh bạ, Cài đặt; không trang nào cuộn ngang | 10/10 đạt sau khi sửa BUG-083 (tệp đính kèm) và BUG-084 (tràn ngang, nút soạn trên điện thoại) | ✅ |
| PSL-01 | Cập nhật Public Suffix List định kỳ (PROMPT 2.3, 9) | `PublicSuffixFeedTests`: máy chủ giả trả bản cắt cụt, trang HTML, bản đầy đủ có thêm một hậu tố riêng, rồi 304 | Bản hỏng bị từ chối, danh sách đang dùng giữ nguyên; bản đầy đủ được dùng ngay cho tên miền tổ chức DMARC; lần kiểm sau gửi If-Modified-Since; bản sao trong S3 được nạp lại sau khởi động; `BM_PSL_URL=off` tắt tải | Như mong đợi (test bắt được lỗi ghi S3 thiếu `UseChunkEncoding=false` trên Garage, đã sửa) | ✅ |
| JOB-01 | Hangfire và Dashboard (PROMPT 1, 9) | `AdminConsoleTests.Jobs_run_on_hangfire…`; nút "Việc nền" ở Quản trị › Tổ chức (quản trị nền tảng) | Việc định kỳ có trong storage (schema `hangfire`) và chạy được; `/hangfire` đóng khi không có cookie, cookie giả, hoặc quản trị viên tổ chức; quản trị nền tảng nhận cookie HttpOnly/SameSite=Strict/Path=/hangfire và mở được trang; CSP không cho script inline | Như mong đợi (test bắt hai lỗi trước khi xong: chính sách xác thực mặc định của API chặn trang, bộ lọc "chỉ nội bộ" của Hangfire) | ✅ |
| OL-11 | Quick Steps | E2E `quicksteps.spec.ts` (1366 và 375 px): Quản lý Quick Steps → bước mới "đã đọc + gắn cờ" → lưu → chạy trên hội thoại; `ContactFormatTests.Favorites_and_quick_steps…`; `QuickSteps.test.ts` | Thao tác chạy theo thứ tự; bước chuyển hội thoại khỏi danh sách thì đóng ngăn đọc; máy chủ chỉ giữ thao tác có thể hoàn tác (bỏ xoá vĩnh viễn, báo rác), tối đa 20 bước × 5 thao tác | Như mong đợi | ✅ |
| OL-12 | Dọn dẹp hội thoại (Clean Up) | `SendingTests.Clean_up_finds…` (ivan gửi, jiwoo trả lời có trích dẫn), `RedundancyTests` | Thư gốc được báo thừa; người khác không đọc được (404); thư cùng giây xếp theo thứ tự trong hội thoại | Như mong đợi (lần đầu chập chờn: hai thư trùng giây ở header Date, đã sửa) | ✅ |
| OL-13 | Trợ lý lập lịch (rảnh/bận) | E2E `calendar.spec.ts` (hoa bận 10:00–10:30; minh mời hoa lúc 10:15 rồi 11:00), `CalendarTests.Free_busy…` | "bận giờ này" rồi "rảnh"; khoảng bận gộp lại, không lộ tiêu đề; ngoài tổ chức `known=false`; quá 31 ngày bị từ chối | Như mong đợi (bắt được BUG-085) | ✅ |
| OL-14 | Chia sẻ lịch (Xem / Sửa) | `CalendarTests.Sharing_a_calendar…` | Chưa chia sẻ: 404; Được xem: đọc được chi tiết, không thêm được; Được sửa: thêm/xoá được, không lập họp có khách; ngừng chia sẻ: đóng lại; không chia sẻ được ra ngoài tổ chức hay cho chính mình | Như mong đợi (giao diện chưa chụp màn hình: dev server bị dừng vì máy thiếu RAM) | ⚠️ |
| OL-15 | Xuất bản lịch (ICS) | `CalendarTests.Published_calendar_link…` | BUSY: chỉ "Busy", không tiêu đề/địa điểm/khách; FULL: có tiêu đề, đọc lại được bằng ICalendar.Parse; link chỉ hiện một lần; tạo lại/gỡ thì link cũ 404 | Như mong đợi; menu đã chụp màn hình, e2e đạt | ✅ |
| OL-16 | Sắp xếp danh sách (người gửi / tiêu đề / kích thước) | `ConversationPagingTests.Sorting_by…` | Đi hết các trang gặp mỗi hội thoại đúng một lần theo đúng thứ tự (A→Z không phân biệt hoa thường, lớn nhất trước); vượt CountCap thì theo ngày và `sort=date` | Như mong đợi (giao diện chưa chụp: dev server dừng vì thiếu RAM) | ⚠️ |
| E2E-02 | Sắp xếp danh sách + giao việc (tests/e2e/specs/outlook-extras.spec.ts) | Playwright, 1366×768 và 375×812 | 18/18 đạt ở 1366×768 và 375×812 (2026-11-24) | ✅ |
| OL-17 | Lọc Gửi cho tôi, Lịch biểu, Gửi lịch qua email | `outlook-extras.spec.ts`, `CalendarTests` (export.ics), `calendarMail.test.ts` | Chỉ lịch của mình, đúng khoảng ngày, BUSY không lộ tiêu đề, > 62 ngày bị từ chối, người lạ 401 | Như mong đợi; ảnh chụp 1366 và 375 px | ✅ |
| OL-18 | Chia sẻ danh bạ, Họp/Theo dõi từ liên hệ, nhắc việc | `OrganizeTests.Shared_contacts…`, `TaskTests`, `tasksApi.test.ts`, e2e | Chỉ xem, người ngoài/khác tổ chức 404, ngừng chia sẻ thì mất quyền; giờ nhắc lưu UTC, nhắc một lần | Như mong đợi; ảnh chụp 1366 và 375 px | ✅ |
| E2E-03 | Bộ e2e đầy đủ sau đợt Outlook cuối | Playwright, 1366×768 và 375×812 | 22/22 đạt | Như mong đợi | ✅ |
