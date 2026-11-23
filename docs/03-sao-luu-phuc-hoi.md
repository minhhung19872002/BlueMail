# 03 — Sao lưu và phục hồi

## 1. Sao lưu gì

| Thành phần | Ở đâu | Cách sao lưu |
|---|---|---|
| CSDL PostgreSQL | volume `pgdata` | `pg_dump -Fc` (một điểm nhất quán) |
| Nội dung thư, tệp lớn, bản xuất | S3 (Garage) bucket `bm-messages`, `bm-attachments-large`, `bm-exports` | `rclone sync` (đối tượng vẫn mã hoá AES-256-GCM theo khoá tổ chức) |
| **Bí mật** (`BM_MASTER_KEY`, khoá OIDC, mật khẩu CSDL/S3) | `deploy/.env` | **Thủ công, ngoài máy**, một lần và sau mỗi lần đổi — không nằm trong bản sao lưu |
| Chứng chỉ | volume Caddy | Không cần: Caddy xin lại |
| Valkey | — | Không cần (bộ đếm, sự kiện tạm) |

Thiếu `BM_MASTER_KEY` thì bản sao lưu **vô dụng** (không giải mã được khoá tổ chức, nên không đọc được thư nào).

## 2. Chạy sao lưu

```sh
sh scripts/backup.sh deploy/.env /srv/backups      # BM_BACKUP_KEEP=7 mặc định
```

Kịch bản dump CSDL **trước**, rồi chép các bucket. Thứ tự này an toàn vì đối tượng S3 chỉ bị xoá rất lâu sau khi không
còn hàng nào trỏ tới (docs/00 D22): mọi đối tượng mà bản dump nhắc tới vẫn còn khi chép. Mỗi bản sao lưu là một thư mục
`<thời điểm UTC>/` gồm `bluemail.dump`, `s3/<bucket>/…`, `manifest.txt` (phiên bản, thời gian, số đối tượng). Chỉ giữ
`BM_BACKUP_KEEP` bản mới nhất.

Lịch gợi ý (cron máy chủ): mỗi đêm `backup.sh`, rồi đẩy thư mục mới sang nơi khác (máy khác, S3 khác, ổ ngoài) — bản
sao lưu nằm cùng máy không chống được hỏng máy. `rclone sync` chỉ chép đối tượng mới nên các lần sau nhanh.

## 3. Phục hồi

```sh
sh scripts/restore.sh /srv/backups/20261123T170247Z deploy/.env
```

Yêu cầu: `deploy/.env` có **cùng** `BM_MASTER_KEY` (và khoá OIDC nếu muốn phiên đăng nhập cũ còn hiệu lực). Trên máy
mới: chép `.env`, `docker compose … up -d postgres valkey s3`, `sh scripts/s3-init.sh deploy/docker-compose.yml deploy/.env`,
rồi `restore.sh`. Kịch bản dừng mọi tiến trình BlueMail, tạo lại CSDL từ bản dump, chép bucket về, khởi động lại.
Phục hồi **thay thế** dữ liệu hiện có của bản cài đó.

Sau phục hồi: đăng nhập web, mở vài thư cũ có tệp đính kèm, kiểm IMAP; thư đến trong lúc hệ thống dừng được bên gửi
thử lại (lỗi tạm `4xx`), không mất.

## 4. Diễn tập đã chạy (Phase 13, 2026-11-23)

Trên bản chạy thật cục bộ (`deploy/docker-compose.yml`, 13 dịch vụ, dữ liệu mẫu gửi qua SMTP):

| Bước | Kết quả |
|---|---|
| Trước sao lưu | 48 thư, 25 người dùng, 48 blob; IMAP: Hộp thư đến của lan có 12 thư, SHA-256 toàn bộ nội dung `4e3ba133…498e` |
| `backup.sh` | 16 giây; dump 441 KB; 48 đối tượng |
| Phá huỷ | `docker compose down -v` (xoá cả volume CSDL và S3) |
| Cài lại + `restore.sh` | 49 giây tổng (khởi tạo S3 + phục hồi 32 giây) |
| Sau phục hồi | 48 thư, 25 người dùng, 48 blob; IMAP: 12 thư, **cùng SHA-256** — nội dung giải mã đúng từng byte; thư mẫu không bị gửi lại |

Ước lượng cho cỡ thật: pg_dump/pg_restore ~1–2 phút mỗi GB dữ liệu CSDL trên SSD; đồng bộ S3 lần đầu bị giới hạn bởi
đĩa/mạng (~100–200 MB/s cục bộ). 2.000 hộp thư, 3–6 TB S3 (docs/00 Z5): lần đầu tính bằng giờ, các lần sau chỉ phần mới.
Chạy lại diễn tập mỗi quý và ghi số đo vào đây.
