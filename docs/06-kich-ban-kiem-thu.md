# 06 — Kịch bản kiểm thử (Test scenarios)

## Môi trường kiểm thử dev

```sh
# Khởi động (profile phase0 thêm bộ hứng thư ở 10.77.0.25 cho tới khi có BlueMail.Mx)
docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev --profile phase0 up -d --build
MSYS_NO_PATHCONV=1 sh scripts/dev-s3-init.sh          # tạo bucket + khoá S3 (idempotent)
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
