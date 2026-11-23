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

## NuGet

| Gói | Phiên bản | Giấy phép | Lý do |
|---|---|---|---|
| Microsoft.EntityFrameworkCore | 10.0.15 | MIT | ORM |
| Microsoft.EntityFrameworkCore.Design | 10.0.15 | MIT | Công cụ migration (chỉ lúc build) |
| Microsoft.EntityFrameworkCore.Relational | 10.0.15 | MIT | ORM |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.4 | PostgreSQL License | Trình cung cấp EF cho PostgreSQL |
| Npgsql | 10.0.4 | PostgreSQL License | Trình kết nối PostgreSQL (đường nóng SMTP/IMAP) |
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 | Tên bảng/cột snake_case |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.15 | MIT | ASP.NET Core Identity |
| Microsoft.AspNetCore.DataProtection.EntityFrameworkCore | 10.0.15 | MIT | Lưu khoá Data Protection trong PostgreSQL |
| HtmlSanitizer | 11.0.26 | MIT | Lọc HTML thư đến phía máy chủ (PROMPT 1, 6.3); kéo theo AngleSharp 1.10.0 và AngleSharp.Css 1.4.0 (MIT) |
| OpenIddict.AspNetCore | 7.9.0 | Apache-2.0 | Máy chủ OIDC (BlueID) |
| OpenIddict.EntityFrameworkCore | 7.9.0 | Apache-2.0 | Lưu ứng dụng/thẻ OIDC |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.15 | MIT | API kiểm thẻ truy cập; IMAP/SMTP kiểm thẻ XOAUTH2/OAUTHBEARER (JsonWebTokenHandler) |
| MediatR | 12.5.0 | Apache-2.0 | CQRS (bản Apache cuối, không nâng lên 13+) |
| FluentValidation | 12.2.0 | Apache-2.0 | Kiểm dữ liệu vào |
| FluentValidation.DependencyInjectionExtensions | 12.2.0 | Apache-2.0 | Đăng ký validator |
| Serilog.AspNetCore | 10.0.0 | Apache-2.0 | Log có cấu trúc |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Log ra tệp |
| Serilog.Sinks.PostgreSQL | 2.3.0 | MIT | Log vào PostgreSQL |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | MIT | Băm mật khẩu Argon2id (BCL chưa có Argon2; docs/00 D2) |
| StackExchange.Redis | 3.4.35 | MIT | Kết nối Valkey |
| AWSSDK.S3 | 4.1.2 | Apache-2.0 | Kết nối kho S3 |
| Swashbuckle.AspNetCore | 10.3.1 | MIT | Swagger |
| Microsoft.Extensions.Caching.Memory | 10.0.15 | MIT | Đệm trong bộ nhớ |
| Microsoft.NET.Test.Sdk | 18.12.0 | MIT | Kiểm thử |
| xunit | 2.9.3 | Apache-2.0 | Kiểm thử |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | Kiểm thử |
| FluentAssertions | 7.2.0 | Apache-2.0 | Kiểm thử (bản ≥ 8 là thương mại) |
| Testcontainers.PostgreSql | 4.17.0 | MIT | Kiểm thử tích hợp |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.15 | MIT | Kiểm thử tích hợp |
| Microsoft.AspNetCore.SignalR.Client | 10.0.15 | MIT | Kiểm thử tích hợp: máy khách SignalR thật nối vào hub thư |
| SharpFuzz | 2.3.0 | MIT | Fuzzing (libFuzzer/AFL) cho bộ phân tích tự viết |

## npm

| Gói | Phiên bản | Giấy phép | Lý do |
|---|---|---|---|
| @ant-design/icons | 5.6.1 | MIT | Biểu tượng |
| @microsoft/signalr | 11.0.0 | MIT | Máy khách thời gian thực (PROMPT 1: SignalR) |
| dompurify | 3.5.0 | MPL-2.0 OR Apache-2.0 (dùng theo Apache-2.0) | Lọc HTML thư lần hai phía trình duyệt (PROMPT 1) |
| pdfjs-dist | 6.5.438 | Apache-2.0 | Xem trước PDF trong trình duyệt (PROMPT 1, 6.3) |
| @tiptap/react | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/pm | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/starter-kit | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/extension-text-align | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/extension-text-style | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/extension-image | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/extension-table | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| @tiptap/extensions | 3.35.0 | MIT | Trình soạn thư TipTap (PROMPT 1; chỉ phần mở rộng miễn phí) |
| tus-js-client | 4.3.1 | MIT | Tải tệp đính kèm tiếp tục được khi đứt mạng (PROMPT 6.4, giao thức tus) |
| dayjs | 1.11.23 | MIT | Kiểu ngày giờ của bộ chọn ngày Ant Design (đã là phụ thuộc của antd; khai báo trực tiếp vì mã dùng kiểu của nó) |
| @tanstack/react-query | 5.108.3 | MIT | Gọi API, đệm |
| antd | 5.29.3 | MIT | Thư viện giao diện |
| i18next | 27.2.4 | MIT | Đa ngôn ngữ |
| i18next-icu | 2.7.1 | MIT | Cú pháp ICU (số nhiều, biến) |
| intl-messageformat | 12.2.1 | BSD-3-Clause | Bộ dịch ICU cho i18next-icu |
| oidc-client-ts | 3.5.0 | Apache-2.0 | Đăng nhập OIDC code + PKCE |
| react | 18.3.1 | MIT | UI |
| react-dom | 18.3.1 | MIT | UI |
| react-i18next | 18.0.3 | MIT | Đa ngôn ngữ cho React |
| react-router-dom | 6.30.6 | MIT | Định tuyến |
| zustand | 5.1.0 | MIT | Trạng thái giao diện |
| @testing-library/react | 16.3.3 | MIT | Kiểm thử |
| @types/node | 24.19.2 | MIT | Kiểu cho phép thử quét |
| @types/react | 18.3.31 | MIT | Kiểu |
| @types/react-dom | 18.3.7 | MIT | Kiểu |
| @vitejs/plugin-react | 6.3.0 | MIT | Build |
| jsdom | 31.0.1 | MIT | Kiểm thử |
| typescript | 5.9.3 | Apache-2.0 | Build |
| vite | 8.3.8 | MIT | Build |
| vitest | 5.1.1 | MIT | Kiểm thử |

## Cảnh báo bảo mật đã đánh giá

| Gói | Cảnh báo | Đánh giá | Việc tiếp |
|---|---|---|---|
| react-router-dom 6.30.6 | GHSA-wrjc-x8rr-h8h6 (chuyển hướng mở qua `\` trong `<Link>`/`useNavigate`), GHSA-337j-9hxr-rhxg (SSR hydration) | Không khai thác được: web chỉ điều hướng tới đường dẫn nội bộ dựng từ id; không dùng SSR. Bản sửa là react-router 7 (đổi API) | Nâng lên 7 ở Phase 13 (hoàn thiện), chạy lại toàn bộ kiểm thử web |

| dkimpy-milter (gói Debian, chỉ container đối chứng doitac.test) | 1.2.3 | BSD-2-Clause / Zlib | Kiểm DKIM thư đến Postfix đối chứng (BUG-027); không có trong image chạy thật |

| rclone/rclone (image) | 1.71.2 | MIT | Chỉ trong `scripts/backup.sh` / `restore.sh` (chép bucket S3), không trong tiến trình chạy |
