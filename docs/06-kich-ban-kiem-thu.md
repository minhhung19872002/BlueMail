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

Chạy: `dotnet test tests/BlueMail.InteropTests` (cần mạng dev đang chạy: container `tools`, CoreDNS, Postfix,
bộ hứng `phase0`); `dotnet test tests/BlueMail.FuzzTests` (mặc định 1 500 lần/đích; chạy dài:
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
