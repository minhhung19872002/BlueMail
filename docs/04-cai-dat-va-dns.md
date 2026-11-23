# 04 — Cài đặt và DNS

## 1. Máy chủ

- Linux x86-64 có Docker Engine 27+ và Compose v2; tối thiểu 4 nhân / 8 GB / SSD (khuyến nghị 8 nhân / 16 GB cho
  ~2.000 hộp thư, `docs/00` Z5 và số đo `docs/06` Phase 13).
- Một địa chỉ IPv4 công khai (IPv6 tuỳ chọn) **mở cổng 25 ra ngoài** và có thể đặt **PTR** (`docs/00` Z6: nhiều nhà
  cung cấp đám mây chặn cổng 25 — nếu vậy dùng máy chủ chuyển tiếp, `BM_SMARTHOST`).
- Cổng vào: 80, 443 (web, Caddy), 25 (MX), 465 và 587 (gửi thư từ phần mềm), 993 (IMAP), tuỳ chọn 143.

## 2. Tên máy và DNS của nền tảng

Ví dụ nền tảng `bluemail.vn`, IP `203.0.113.10`:

| Bản ghi | Giá trị |
|---|---|
| `mail.bluemail.vn` A | 203.0.113.10 (web; `BM_WEB_HOST`) |
| `id.bluemail.vn` A | 203.0.113.10 (đăng nhập BlueID; `BM_ID_HOST`) |
| `mx.bluemail.vn` A | 203.0.113.10 (`BM_MAIL_HOSTNAME`, cũng là HELO khi gửi) |
| `imap.bluemail.vn`, `smtp.bluemail.vn` A | 203.0.113.10 (phần mềm thư) |
| PTR của 203.0.113.10 | `mx.bluemail.vn` (đặt ở nhà cung cấp IP) |
| `_spf.bluemail.vn` TXT | `v=spf1 ip4:203.0.113.10 -all` (mọi IP gửi) |
| `bluemail.vn` MX | `10 mx.bluemail.vn.` |
| `_mta-sts.<miền>` TXT, `mta-sts.<miền>` A | xem trang Tên miền |
| `_smtp._tls.<miền>` TXT | `v=TLSRPTv1; rua=mailto:tlsrpt@<miền>` |

Tên miền của từng tổ chức: trình hướng dẫn trong trang Quản trị → Tên miền in ra đúng các bản ghi (MX, SPF có
`include:_spf.<nền tảng>`, DKIM, DMARC, MTA-STS). DNSSEC cho tên miền của mình là tuỳ chọn; muốn bên gửi khác dùng
DANE khi gửi tới mình thì ký vùng và công bố `_25._tcp.mx.<nền tảng>` TLSA 3 1 1 theo khoá chứng chỉ MX.

## 3. Cài mới

```sh
git clone https://github.com/minhhung19872002/BlueMail.git && cd BlueMail
sh scripts/install.sh mail.bluemail.vn id.bluemail.vn mx.bluemail.vn admin@bluemail.vn admin@bluemail.vn
```

`install.sh` tạo `deploy/.env` với bí mật ngẫu nhiên (in ra mật khẩu quản trị đầu tiên — buộc đổi khi đăng nhập),
build image, khởi tạo S3 (Garage) và chạy `docker compose up -d`. Tham số thứ tư là `BM_TLS`: email tài khoản ACME
(Let's Encrypt) cho máy thật, hoặc `internal` (chứng chỉ tự ký, chỉ để thử).

**Sao lưu ngay `deploy/.env` ra nơi an toàn ngoài máy**: thiếu `BM_MASTER_KEY` thì không giải mã được thư.

Muốn có dữ liệu mẫu (hai tổ chức `bluemail.test`, `acme.test`, người dùng nhiều nước, thư mẫu gửi thật qua SMTP):
đặt `BM_SEED_SAMPLE_DATA=true` và `BM_SEED_USER_PASSWORD` trong `.env` trước khi chạy.

## 4. Các dịch vụ (`deploy/docker-compose.yml`)

| Dịch vụ | Vai trò |
|---|---|
| `postgres`, `valkey`, `s3` (Garage) | Dữ liệu, bộ nhớ đệm/sự kiện, nội dung thư (mã hoá theo tổ chức) |
| `identity` | BlueID (OIDC), chạy migration CSDL khi khởi động |
| `api` | REST API + SignalR (`BM_ROLE=api`) |
| `worker` | Việc nền (`BM_ROLE=worker`, cùng image) |
| `mx` | Nhận thư cổng 25 |
| `submission` | SMTP 465/587 cho phần mềm thư |
| `imap` | IMAP 993/143 |
| `outbound` | Gửi thư ra ngoài, DSN, TLS-RPT; dữ liệu mẫu (nếu bật) |
| `web`, `caddy` | Ứng dụng web; HTTPS, chứng chỉ tự động |

Chứng chỉ của `mx`/`imap`/`submission` do Caddy xin và gia hạn (khối tên máy thư trong `deploy/caddy/Caddyfile`); ba
dịch vụ đọc từ volume dữ liệu Caddy (chỉ đọc) và tự nạp lại khi chứng chỉ đổi. Với `BM_TLS=internal` đặt thêm
`BM_CERT_ISSUER=local`. Dùng chứng chỉ riêng: đặt `BM_TLS_CERT`/`BM_TLS_KEY` (PEM) cho từng dịch vụ.

**IP gửi**: `outbound` gửi từ địa chỉ của máy. Nhiều IP công khai (dải IP, `docs/07`): mỗi địa chỉ phải có trên máy
(network host hoặc macvlan) để `outbound` bind được; khai báo IP + HELO trong Quản trị → Hạ tầng gửi thư.

## 5. Nâng cấp

```sh
git pull && sh scripts/deploy.sh deploy/.env
```

Build image gắn mã commit, chạy bản mới, kiểm `/health`; không lành thì tự quay về bản trước. Giữ image của bản đang
chạy và bản trước, xoá cũ hơn. Migration CSDL chạy khi `identity` khởi động (chỉ thêm, không sửa migration đã phát hành). Migration tạo chỉ mục trên
bảng thư lớn có thể mất nhiều phút và chặn ghi thư trong lúc đó (thư đến được bên gửi thử lại); hạn mỗi lệnh 60 phút
(`BM_DB_MIGRATE_TIMEOUT_MINUTES`). Nâng cấp vào giờ thấp điểm.

## 6. Kiểm tra sau cài

- `https://<web>` đăng nhập được; Quản trị → Tên miền: mọi bản ghi xanh.
- Gửi thử tới một hộp Gmail: xem "Hiện bản gốc" → `SPF: PASS`, `DKIM: PASS`, `DMARC: PASS`.
- `openssl s_client -connect mx.<nền tảng>:25 -starttls smtp` thấy chứng chỉ đúng.
- `/health/ready` của từng dịch vụ (Docker healthcheck đã dùng): `docker compose ps` đều `healthy`.

## 7. Biến môi trường

Danh sách đầy đủ có chú thích trong `deploy/.env.example`. Thiếu biến bắt buộc thì tiến trình dừng và nói rõ biến nào.

## Máy chủ dùng chung (VM BlueStar 14.225.83.93) — chuẩn bị cho P12-12

- Image không build trên máy chủ: `.github/workflows/images.yml` build `bluemail-server` và `bluemail-web` trên GitHub Actions,
  đẩy lên `ghcr.io/minhhung19872002/bluemail-<tên>:<sha>` (và `:main`).
- Chạy: `docker compose --env-file deploy/.env -f deploy/docker-compose.yml -f deploy/vm/docker-compose.vm.yml up -d --no-build`
  với `BM_IMAGE_REPO=ghcr.io/minhhung19872002`, `BM_VERSION=<sha>`, `BM_PLATFORM_DOMAIN=bm.bluestar.com.vn`,
  `BM_MAIL_HOSTNAME=mail.bm.bluestar.com.vn`, `BM_WEB_HOST=bm.bluestar.com.vn`, `BM_ID_HOST=id.bm.bluestar.com.vn`.
- Gói GHCR công khai; trên VM kéo bằng cấu hình docker trống (`docker --config "$(mktemp -d)" pull …`), vì `~/.docker` của VM giữ thông tin đăng nhập ghcr cũ làm truy vấn manifest thất bại (đã kiểm 2026-11-24).
- Overlay `deploy/vm/docker-compose.vm.yml`: tắt Caddy riêng; mx/submission/imap đọc chứng chỉ từ volume của Caddy chung
  (`bluedental_caddy_data`, chỉ đọc); giới hạn RAM tổng ~4 GB, Postgres `shared_buffers=256MB`.
- Caddy chung: chép `deploy/vm/bluemail.caddy` vào `~/proxy/sites/`, `docker network connect bluemail_default proxy-caddy`,
  `caddy validate` rồi `caddy reload` (không tạo lại container caddy).
- Điều kiện trước khi gửi thử: PTR 14.225.83.93 → `mail.bm.bluestar.com.vn` (VNPT) và các bản ghi DNS ở docs/06 P12-12,
  thêm A cho `bm`, `id.bm`, `smtp.bm`, `imap.bm` → 14.225.83.93.
- Trên VM (đã chạy 2026-11-24): `~/BlueMail/bm.sh` gói lệnh compose (base + `vm` overlay + `deploy/vm/local.yml` cục bộ, không trong git: submission/imap dùng chứng chỉ `mail.bm` khi `smtp.bm`/`imap.bm` chưa có DNS). `~/proxy/sites/bluemail.caddy` hiện chỉ có `mail.bm`; khi đủ DNS thay bằng `deploy/vm/bluemail.caddy` và `docker network connect bluemail_default proxy-caddy`. Mật khẩu quản trị đầu tiên nằm trong `deploy/.env` trên VM.
