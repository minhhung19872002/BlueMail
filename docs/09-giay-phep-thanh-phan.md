# 09 — Giấy phép thành phần (Component licences)

Mọi gói NuGet/npm và image chạy thật phải có trong bảng này trước khi dùng. Phép thử
`LicenceAllowListTests` (Phase 1) đối chiếu `Directory.Packages.props` và `web/package.json` với bảng này.

## Image chạy thật

| Thành phần | Phiên bản | Giấy phép | Lý do |
|---|---|---|---|
| PostgreSQL (`postgres:16.16-alpine`) | 16.16 | PostgreSQL License | CSDL |
| Garage (`dxflrs/garage:v2.5.0`) | 2.5.0 | AGPL-3.0 — chạy nguyên bản, dịch vụ riêng, không sửa | Kho S3 (docs/00 Z3) |
| Valkey (`valkey/valkey:8.1.12-alpine3.24`) | 8.1.12 | BSD-3-Clause | Đệm, sự kiện, backplane SignalR |
| Caddy (`caddy:2.10.2-alpine`) | 2.10.2 | Apache-2.0 | Reverse proxy HTTPS |

## Chỉ dùng trong kiểm thử (không có trong image chạy thật)

| Thành phần | Phiên bản | Giấy phép |
|---|---|---|
| CoreDNS | 1.12.4 | Apache-2.0 |
| Postfix (Debian bookworm) | 3.7.11 | IPL-1.0 / EPL-2.0 |
| OpenDKIM (Debian bookworm) | 2.11 | BSD-3-Clause / Sendmail |
| ClamAV | 1.5.5 | GPL-2.0 |
| Dovecot imaptest | digest `sha256:34e3a7…` | MIT |
| swaks | 20201014 | GPL-2.0 |
| dkimpy | 1.1.8 | BSD-like (zlib) |
| authheaders / authres | 0.16.3 / 1.2.0 | MIT / Apache-2.0 |
| checkdmarc | 6.0.4 | Apache-2.0 |
| dnspython | 2.9.2 | ISC |
| aiosmtpd | 1.4.6 | Apache-2.0 |
| vobject | 0.9.9 | Apache-2.0 |
| Python | 3.12.12 | PSF |

## NuGet / npm

(Phase 1 trở đi.)
