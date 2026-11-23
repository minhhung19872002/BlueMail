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
| RFC 5322 | `BlueMail.Mime` | tiêu đề, gấp dòng, địa chỉ (nhóm, chú thích, route cũ), ngày (cả dạng cũ 4.3), Message-ID | sinh phần cục bộ quoted-string | `MimeCorpusTests`, `AddressTests`, `DateTests` |
| RFC 2045/2046 | `BlueMail.Mime` | multipart lồng, preamble/epilogue, message/rfc822, digest, base64/QP khoan dung + luồng | CTE `binary` coi như identity | `MimeCorpusTests`, `ParserTests`, `TransferEncodingTests` |
| RFC 2047 | `BlueMail.Mime` | B/Q, ghép từ kề nhau theo byte, mã hoá theo grapheme | — | `EncodedWordTests` |
| RFC 2231 | `BlueMail.Mime` | nối đoạn, bảng mã/ngôn ngữ, ưu tiên tham số mở rộng | — | `ParameterTests`, `Rfc2231_segments_never_split_an_escape` |
| RFC 2183 / 2392 | `BlueMail.Mime` | inline/attachment, filename, Content-ID | tham số size/date | `MimeCorpusTests` |
| RFC 3676 | `BlueMail.Mime` | giải format=flowed (DelSp, trích dẫn, chữ ký) | mã hoá flowed | `Format_flowed_joins_soft_breaks…` |
| RFC 6532 | `BlueMail.Mime` | tiêu đề UTF-8 thô; báo cần SMTPUTF8 | — | `eai-utf8-headers`, `Smtputf8_needed_only…` |
| RFC 3464 | `BlueMail.Mime` | đọc message/delivery-status | — | `Delivery_status_is_read` |
| RFC 1035 / 3596 / 6891 / 7766 / 2308 | `BlueMail.Dns` | xem `src/BlueMail.Dns/README.md` | DNSSEC (Phase 12) | `DnsInteropTests`, `DnsWireTests` |
| RFC 6376 | `BlueMail.Dkim` | ký/kiểm, chuẩn hoá simple/relaxed, bản ghi khoá, ký thừa tiêu đề | z= | `DkimInteropTests`, `DkimCanonicalizationTests` (ví dụ RFC 3.4.5) |
| RFC 8301 | `BlueMail.Dkim` | từ chối rsa-sha1, khoá < 1024 bit; ký ≥ 2048 bit | — | `DkimVerifierTests` |
| RFC 8463 | `BlueMail.Dkim` | nhận diện, báo neutral | ký/kiểm Ed25519 (BCL không có) | `Rsa_sha1_fails_and_ed25519_is_neutral` |
| RFC 8617 | `BlueMail.Dkim` | kiểm chuỗi ARC, niêm chặng mới | — | `ArcInteropTests` |
| RFC 1952 / ZIP APPNOTE / RAR 4-5 / MS-CFB / ISO 9660 | `BlueMail.Scanner` | xem `src/BlueMail.Scanner/README.md` | liệt kê 7z có thư mục nén LZMA | `ScannerSampleTests`, `ScannerClamAvComparisonTests` |
| RFC 5321 | `BlueMail.Protocols.Smtp`, `BlueMail.Mx` | máy trạng thái EHLO/HELO/MAIL/RCPT/DATA/RSET/NOOP/QUIT/VRFY(252)/EXPN(502)/HELP; dot-unstuffing; giới hạn dòng, người nhận (452), lỗi lặp (421); Received (4.4); từ chối tại RCPT | AUTH (Phase 4, submission), DSN NOTIFY/RET chỉ nhận chưa dùng (Phase 5) | `SmtpSessionTests`, `SmtpTransportTests`, `mx_checks.py` |
| RFC 2920 PIPELINING | `BlueMail.Protocols.Smtp` | trả lời theo thứ tự cho nhóm lệnh | — | `Pipelined_transaction…`, P3-08 |
| RFC 1870 SIZE | idem | quảng bá, từ chối ở MAIL (552), đếm thật khi DATA/BDAT | — | P3-05 |
| RFC 6152 8BITMIME / RFC 3030 CHUNKING | idem | BODY=7BIT/8BITMIME/BINARYMIME; BDAT nhiều khúc, chỉ BDAT/RSET/QUIT/NOOP giữa chừng | — | `Bdat_rules`, `Bdat_chunks_are_joined…`, P3-07 |
| RFC 6531 SMTPUTF8 | idem | phong bì UTF-8, 553 5.6.7 khi thiếu tham số, `with UTF8SMTP(S)` | hạ cấp khi gửi đi (Phase 5) | P3-06 |
| RFC 3207 STARTTLS | idem | TLS 1.2+, bỏ dữ liệu tiêm trước bắt tay, đặt lại phiên sau TLS | DANE/MTA-STS khi gửi đi (Phase 5) | `Starttls_injection…`, P3-09 |
| RFC 2034 ENHANCEDSTATUSCODES | idem | mã x.y.z ở mọi trả lời sau EHLO | — | `Reply_formatting_multiline_and_enhanced` |
| RFC 8601 | `BlueMail.Mx` | thêm Authentication-Results (spf, dkim, dmarc, arc) ở đầu bản lưu | xoá A-R giả mạo cùng authserv-id (hiện chỉ không tin, Phase 6) | P3-01 |
| RFC 7208 SPF | `BlueMail.Auth` | mọi cơ chế và modifier, giới hạn 10 tra cứu DNS / 2 void, macro, exp= | — | `SpfSuiteTests` (bộ thử chính thức rfc7208-tests.yml) |
| RFC 7489 DMARC | `BlueMail.Auth` | tìm chính sách ở From rồi tên miền tổ chức (PSL), căn chỉnh relaxed/strict, pct, sp= | báo cáo rua/ruf (Phase 7) | `DmarcPslTests`, P3-11 |
