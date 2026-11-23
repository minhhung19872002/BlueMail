# 10 — Tuân thủ RFC (RFC compliance)

Mỗi RFC: phạm vi đã làm, chưa làm, bằng chứng kiểm thử. Cập nhật sau mỗi phase.

| RFC / chuẩn | Thành phần | Đã làm | Chưa làm | Bằng chứng |
|---|---|---|---|---|
| RFC 6238 TOTP / RFC 4226 HOTP | `Infrastructure/Security/Totp` | HMAC-SHA1, bước 30 s, 6 chữ số, lệch ±1 bước, chống dùng lại bước | SHA-256/512 (ứng dụng xác thực hầu như không hỗ trợ) | `TotpTests.Matches_rfc6238_vectors` (vector Phụ lục B) |
| RFC 4648 base32 | `Totp.Base32` | Mã hoá không đệm | Giải mã (chưa cần) | `Base32_matches_rfc4648_vectors_without_padding` (vector mục 10) |
| RFC 6749 / OIDC Core | BlueID (OpenIddict) | Authorization code, refresh token, end-session, userinfo, discovery | Client credentials, device flow (chưa cần) | `LoginTests.Web_login_through_BlueID…` (luồng thật qua trang đăng nhập) |
| RFC 7636 PKCE | BlueID | Bắt buộc, S256 | — | `OidcLogin.LoginAsync` |
| RFC 9068 JWT access token | BlueID → API | `typ at+jwt`, `aud bluemail-api` | — | API chỉ nhận `at+jwt`/`JWT` có đúng issuer/audience |
| RFC 9457 Problem Details | API, BlueID | `type/title/status/code/errors` | — | `Api_without_token_answers_401_problem_json` |
| RFC 5322 3.4.1 / RFC 6531 3.3 địa chỉ | `Domain/Common/EmailAddress` | dot-atom, phần cục bộ UTF-8 (NFC), độ dài 64/253/320 | quoted-string phần cục bộ (cố ý từ chối khi tạo tài khoản; bộ phân tích MIME ở Phase 2 sẽ đọc được) | `EmailAddressTests` |
| IDNA2008 / UTS #46 | `EmailAddress` (`IdnMapping`, `UseStd3AsciiRules`) | A-label khi lưu, U-label khi hiển thị | Cảnh báo trộn hệ chữ (Phase 8) | `Normalises_to_lower_case_and_a_label` (đối chứng `idna` của Python) |
| WCAG 2.1 AA 1.4.3 | `web/src/theme/tokens.ts` | Tương phản ≥ 4,5:1 mọi cặp chữ/nền ở sáng và tối | Đo trên toàn trang bằng trình duyệt (Phase 13) | `tokens.test.ts` |
