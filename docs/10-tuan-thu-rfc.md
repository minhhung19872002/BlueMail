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
| IDNA2008 / UTS #46 | `EmailAddress` (`IdnMapping`, `UseStd3AsciiRules`) | A-label khi lưu, U-label khi hiển thị | — (cảnh báo trộn hệ chữ đã làm ở Phase 8: `mailView.ts`, luật `SpamRules`) | `Normalises_to_lower_case_and_a_label` (đối chứng `idna` của Python) |
| WCAG 2.1 AA 1.4.3 | `web/src/theme/tokens.ts` | Tương phản ≥ 4,5:1 mọi cặp chữ/nền ở sáng và tối | — (đo toàn trang bằng Lighthouse ở Phase 13: các trang chính 100, BUG-071) | `tokens.test.ts` |
| RFC 5322 | `BlueMail.Mime` | tiêu đề, gấp dòng, địa chỉ (nhóm, chú thích, route cũ), ngày (cả dạng cũ 4.3), Message-ID | sinh phần cục bộ quoted-string | `MimeCorpusTests`, `AddressTests`, `DateTests` |
| RFC 2045/2046 | `BlueMail.Mime` | multipart lồng, preamble/epilogue, message/rfc822, digest, base64/QP khoan dung + luồng; khi tạo thư, message/rfc822 đính kèm chỉ dùng 7bit/8bit (5.2.1) | CTE `binary` coi như identity; thư đính kèm không hợp lệ 8bit gửi thành `.eml` base64 | `MimeCorpusTests`, `ParserTests`, `TransferEncodingTests` |
| RFC 5545 iCalendar | `Features/Calendar/ICalendar` | gấp/mở dòng (75 octet, không cắt giữa UTF-8), tham số có ngoặc kép, TEXT escape, DATE/DATE-TIME (UTC, TZID IANA/Windows, VTIMEZONE STANDARD làm dự phòng, floating), DURATION, sự kiện gốc ưu tiên hơn RECURRENCE-ID | RRULE chỉ hiển thị "lặp lại" (chưa mở rộng các lần); offset DAYLIGHT của VTIMEZONE lạ không dùng | `ICalendarTests` |
| RFC 5546 iTIP / RFC 6047 iMIP | `Features/Calendar/Invites`, `MessageBuilder.Calendar` | đọc REQUEST/REPLY/CANCEL/PUBLISH; gửi REPLY (UID, SEQUENCE, DTSTAMP, ORGANIZER, ATTENDEE;PARTSTAT) dạng text/calendar; method=REPLY trong multipart/alternative | COUNTER/DECLINECOUNTER/REFRESH chưa hỗ trợ | `SendingTests.Meeting_invitation…` |
| RFC 2047 | `BlueMail.Mime` | B/Q, ghép từ kề nhau theo byte, mã hoá theo grapheme | — | `EncodedWordTests` |
| RFC 2231 | `BlueMail.Mime` | nối đoạn, bảng mã/ngôn ngữ, ưu tiên tham số mở rộng | — | `ParameterTests`, `Rfc2231_segments_never_split_an_escape` |
| RFC 2183 / 2392 | `BlueMail.Mime` | inline/attachment, filename, Content-ID | tham số size/date | `MimeCorpusTests` |
| RFC 3676 | `BlueMail.Mime` | giải format=flowed (DelSp, trích dẫn, chữ ký) | mã hoá flowed | `Format_flowed_joins_soft_breaks…` |
| RFC 6532 | `BlueMail.Mime` | tiêu đề UTF-8 thô; báo cần SMTPUTF8 | — | `eai-utf8-headers`, `Smtputf8_needed_only…` |
| RFC 3464 | `BlueMail.Mime` | đọc message/delivery-status | — | `Delivery_status_is_read` |
| RFC 1035 / 3596 / 6891 / 7766 / 2308 | `BlueMail.Dns` | xem `src/BlueMail.Dns/README.md` | — (DNSSEC ở Phase 12, xem dòng RFC 4033–4035) | `DnsInteropTests`, `DnsWireTests` |
| RFC 6376 | `BlueMail.Dkim` | ký/kiểm, chuẩn hoá simple/relaxed, bản ghi khoá, ký thừa tiêu đề | z= | `DkimInteropTests`, `DkimCanonicalizationTests` (ví dụ RFC 3.4.5) |
| RFC 8301 | `BlueMail.Dkim` | từ chối rsa-sha1, khoá < 1024 bit; ký ≥ 2048 bit | — | `DkimVerifierTests` |
| RFC 8463 | `BlueMail.Dkim` | nhận diện, báo neutral | ký/kiểm Ed25519 (BCL không có) | `Rsa_sha1_fails_and_ed25519_is_neutral` |
| RFC 8617 | `BlueMail.Dkim` | kiểm chuỗi ARC, niêm chặng mới | — | `ArcInteropTests` |
| RFC 1952 / ZIP APPNOTE / RAR 4-5 / MS-CFB / ISO 9660 | `BlueMail.Scanner` | xem `src/BlueMail.Scanner/README.md` | liệt kê 7z có thư mục nén LZMA | `ScannerSampleTests`, `ScannerClamAvComparisonTests` |
| RFC 5321 | `BlueMail.Protocols.Smtp`, `BlueMail.Mx` | máy trạng thái EHLO/HELO/MAIL/RCPT/DATA/RSET/NOOP/QUIT/VRFY(252)/EXPN(502)/HELP; dot-unstuffing; giới hạn dòng, người nhận (452), lỗi lặp (421); Received (4.4); từ chối tại RCPT | DSN NOTIFY/RET chỉ nhận, chưa dùng (AUTH đã làm ở Phase 11, submission) | `SmtpSessionTests`, `SmtpTransportTests`, `mx_checks.py` |
| RFC 2920 PIPELINING | `BlueMail.Protocols.Smtp` | trả lời theo thứ tự cho nhóm lệnh | — | `Pipelined_transaction…`, P3-08 |
| RFC 1870 SIZE | idem | quảng bá, từ chối ở MAIL (552), đếm thật khi DATA/BDAT | — | P3-05 |
| RFC 6152 8BITMIME / RFC 3030 CHUNKING | idem | BODY=7BIT/8BITMIME/BINARYMIME; BDAT nhiều khúc, chỉ BDAT/RSET/QUIT/NOOP giữa chừng | — | `Bdat_rules`, `Bdat_chunks_are_joined…`, P3-07 |
| RFC 6531 SMTPUTF8 | idem | phong bì UTF-8, 553 5.6.7 khi thiếu tham số, `with UTF8SMTP(S)` | hạ cấp khi gửi đi: không làm — máy nhận không có SMTPUTF8 thì thư bị trả lại (RFC 6531 3.4 cho phép) | P3-06 |
| RFC 3207 STARTTLS | idem | TLS 1.2+, bỏ dữ liệu tiêm trước bắt tay, đặt lại phiên sau TLS | — (DANE và MTA-STS khi gửi đi đã làm ở Phase 12) | `Starttls_injection…`, P3-09 |
| RFC 2034 ENHANCEDSTATUSCODES | idem | mã x.y.z ở mọi trả lời sau EHLO | — | `Reply_formatting_multiline_and_enhanced` |
| RFC 8601 | `BlueMail.Mx` | thêm Authentication-Results (spf, dkim, dmarc, arc) ở đầu bản lưu | xoá A-R giả mạo cùng authserv-id (hiện chỉ không tin: tiêu đề của BlueMail đứng đầu, giao diện chỉ đọc cột auth_results) | P3-01 |
| RFC 7208 SPF | `BlueMail.Auth` | mọi cơ chế và modifier, giới hạn 10 tra cứu DNS / 2 void, macro, exp= | — | `SpfSuiteTests` (bộ thử chính thức rfc7208-tests.yml) |
| RFC 7489 DMARC | `BlueMail.Auth` | tìm chính sách ở From rồi tên miền tổ chức (PSL), căn chỉnh relaxed/strict, pct, sp= | gửi báo cáo rua cho tên miền khác; báo cáo lỗi ruf. **Đã làm (2026-11-24):** nhận và đọc báo cáo tổng hợp gửi về `dmarc-reports@` (XML, gzip/zip; chặn DTD, trần giải nén 20 MB), lưu theo tổ chức sở hữu tên miền, báo cáo quản trị "Báo cáo DMARC nhận về" — `AggregateReportParserTests`, `AdminConsoleTests.Dmarc_and_tls_reports…` | `DmarcPslTests`, P3-11 |
| RFC 5321 (máy khách) | `SmtpClient`, `BlueMail.Outbound` | EHLO/HELO, MAIL/RCPT/DATA với nhân đôi dấu chấm, kết quả theo từng người nhận, thử MX theo ưu tiên, MX ngầm định, thời gian chờ 4.5.3.2 | PIPELINING phía khách (gửi tuần tự) | `SmtpClientTests`, `SendingTests`, P5-01 |
| RFC 7505 null MX | `MxResolver` | "MX 0 ." → trả lại ngay 5.1.10 | — | `RoutingTests` |
| RFC 8461 MTA-STS | `MtaStsResolver`, `SmtpClient` | TXT id, tải chính sách HTTPS có kiểm chứng chỉ, đệm theo max_age, giữ chính sách cũ khi tải lỗi, lọc MX, bắt buộc TLS + chứng chỉ khớp | — (TLS-RPT ở Phase 12, xem dòng RFC 8460) | `RoutingTests`, `Required_tls_fails_closed…` |
| RFC 3464 / 6522 DSN | `DsnBuilder` | multipart/report, message/delivery-status (Final-Recipient, Action, Status, Remote-MTA, Diagnostic-Code), text/rfc822-headers; lời giải thích tiếng Anh + tiếng người gửi | — | `DsnTests` (đọc lại bằng bộ phân tích DSN của mình) |
| RFC 3030 / 1870 / 6152 / 6531 (máy khách) | `SmtpClient` | BDAT khi có CHUNKING, SIZE, BODY=8BITMIME, SMTPUTF8 (không có thì báo lỗi vĩnh viễn thay vì hạ cấp làm hỏng địa chỉ) | Hạ cấp địa chỉ quốc tế hoá | `SmtpClientTests` |
| RFC 5322 3.6.4 trả lời | `ComposeFromMessageHandler` | In-Reply-To, References (giới hạn 20, giữ phần đầu) | — | `Reply_reply_all_and_forward…` |
| tus 1.0 | `UploadsController` | creation, HEAD, PATCH có kiểm offset, termination | concatenation, checksum | (kiểm qua web) |
| RFC 3834 trả lời tự động | `MailboxRules` | Không trả lời: return-path rỗng, `Auto-Submitted` ≠ no, `Precedence` bulk/list/junk, `List-*`, MAILER-DAEMON/owner-/-request/noreply, chính mình, thư không gửi trực tiếp tới mình (To/Cc); trả lời tới return-path, `MAIL FROM:<>`, `Auto-Submitted: auto-replied`, In-Reply-To/References; mỗi người gửi một lần/4 ngày | — | `VacationRuleTests` (11 ca), `Vacation_replies_…`, kiểm SMTP Phase 7 |
| RFC 8617 ARC (niêm phong khi chuyển tiếp) | `SystemMail.ArcSealAsync` | Thêm bộ i=N với AAR ghi kết quả đã kiểm; không nối chuỗi đã hỏng | — | Postfix nhận `ARC-Seal: i=1 … d=bluemail.test`, DKIM gốc vẫn pass |
| SRS (Sender Rewriting Scheme, bản mô tả của libsrs2) | `Srs` | SRS0, hash HMAC, hạn 21 ngày, giải ngược ở MX (chỉ thư trả về) và ở Outbound | SRS1 (chuyển tiếp nhiều tầng: dùng lại SRS0 bọc địa chỉ) | `SrsTests`, kiểm SMTP Phase 7 |
| RFC 6350 vCard 4.0 | `Contacts/ContactFormats.VCard` | Ghi 4.0 (gấp dòng 75 octet không cắt ký tự UTF-8, escape `\\ , ; \n`, TEL `tel:` URI, PHOTO `data:`); đọc 4.0/3.0/2.1 (nhóm `item1.`, TYPE nhiều giá trị, QUOTED-PRINTABLE + CHARSET, ảnh base64) | Các thuộc tính khác (ADR, BDAY, URL…) bị bỏ qua khi nhập | `VCardTests` |
| RFC 4180 CSV | `ContactCsv` | Trường trong ngoặc kép, dấu phẩy và xuống dòng trong trường, BOM; khuôn Google và Outlook; chặn công thức bảng tính khi xuất | — | `ContactCsvTests` |
| RFC 2369 / RFC 8058 hủy đăng ký | `UnsubscribeHandler` | `List-Unsubscribe` https/mailto; POST một chạm chỉ khi có `List-Unsubscribe-Post` và DKIM pass; không theo chuyển hướng | — | `UnsubscribeAndSettingsTests`, `Unsubscribe_by_mailto_…` |
| RFC 6068 mailto | `UnsubscribeHandler.ParseMailto` | địa chỉ, subject, body | nhiều người nhận (lấy người đầu) | `Unsubscribe_by_mailto_…` |
| RFC 3501 / RFC 9051 IMAP (máy khách) | `Protocols.Imap/ImapClient`, `ImapParser` | Cú pháp phản hồi đầy đủ (atom, số, chuỗi, literal, NIL, danh sách, mã phản hồi), CAPABILITY, LOGIN, EXAMINE, UID SEARCH, UID FETCH (FLAGS, INTERNALDATE, RFC822.SIZE, BODY.PEEK[]), LIST, LOGOUT, tên hộp thư UTF-7 biến thể (§5.1.3) | (máy chủ đã làm ở Phase 11); IDLE, APPEND, STORE ở máy khách (không cần cho nhập) | `ImapParserTests`, `ImapClientInteropTests` (Dovecot vs imaplib), fuzz `imap` |
| RFC 2595 / RFC 8314 TLS cho IMAP | `ImapClient` | TLS ngầm định 993, STARTTLS có kiểm dữ liệu chèn trước TLS; không gửi mật khẩu khi chưa có TLS | — | `ImapClientInteropTests.Wrong_password…` (chứng chỉ không ghim bị từ chối) |
| RFC 4616 SASL PLAIN / RFC 4959 SASL-IR | `ImapClient.LoginAsync` | AUTHENTICATE PLAIN, có SASL-IR khi máy chủ hỗ trợ; UTF-8 an toàn | XOAUTH2 | `ImapClientInteropTests` |
| RFC 6154 SPECIAL-USE, RFC 5258 LIST-EXTENDED | `ImapClient.ListAsync`, `ImportMapping` | `RETURN (SPECIAL-USE)`, \Sent \Drafts \Junk \Trash \All \Archive \Flagged \Important → nhãn | — | `ImportMappingTests`, `ImapClientInteropTests` |
| RFC 7888 LITERAL+, RFC 3516 literal8 | `ImapReader` | Đọc `{n+}`, `~{n}`; gửi `{n+}` khi máy chủ có LITERAL+ | — | `ImapParserTests` |
| RFC 5530 mã phản hồi IMAP | `ImapCommandException` | AUTHENTICATIONFAILED/AUTHORIZATIONFAILED → lỗi đăng nhập rõ ràng | — | `Wrong_password_is_an_authentication_failure…` |
| Gmail IMAP X-GM-EXT-1 | `ImapClient`, `ImportMapping.GmailLabels` | X-GM-LABELS → nhãn hệ thống và nhãn người dùng lồng nhau | X-GM-THRID (luồng được ghép lại bằng tiêu đề) | `ImapParserTests.Gmail_labels…`, `ImportMappingTests` |
| RFC 4155 mbox (mboxrd) | `BlueMail.Mime/Mbox` | Ghi mboxrd; đọc mboxrd và mboxo; ngày dòng "From "; giới hạn cỡ thư | Content-Length (mboxcl) | `MboxInteropTests` (Python mailbox hai chiều), `ImportMappingTests` |

| RFC 3501 IMAP4rev1 (máy chủ) | `BlueMail.Imap` | Mọi lệnh cơ bản; \Recent trong tiến trình; nhãn là thư mục | "Tất cả thư" chưa là thư mục; đăng ký thư mục không tồn tại | `imaptest` 0/460 lệnh cơ bản sai |
| RFC 2177 IDLE, RFC 4315 UIDPLUS, RFC 6851 MOVE, RFC 6154 SPECIAL-USE, RFC 5258 LIST-EXTENDED, RFC 2342 NAMESPACE, RFC 2971 ID, RFC 5161 ENABLE, RFC 3691 UNSELECT, RFC 3348 CHILDREN, RFC 7888 LITERAL+, RFC 4959 SASL-IR | `BlueMail.Imap` | Đủ phần bắt buộc | LIST-EXTENDED: `\NonExistent` | `imaptest`, `imap_submission_checks.py` |
| RFC 7162 CONDSTORE/QRESYNC | `BlueMail.Imap` | MODSEQ, CHANGEDSINCE, UNCHANGEDSINCE + [MODIFIED], SEARCH MODSEQ, SELECT QRESYNC, VANISHED (EARLIER), CLOSED | ESEARCH kèm MODSEQ | `imap_submission_checks.py` (QRESYNC) |
| RFC 4978 COMPRESS=DEFLATE | `BlueMail.Imap` | DEFLATE hai chiều sau OK | — | thủ công |
| RFC 6409 / RFC 8314 SMTP nộp thư | `BlueMail.Submission` | 587 STARTTLS, 465 TLS ngầm định, MAIL trước AUTH bị từ chối, thêm Message-ID/Date, bỏ Bcc, chân thư bắt buộc | — | `imap_submission_checks.py`, `SmtpAuthTests` |
| RFC 4954 SMTP AUTH, RFC 4616 PLAIN, RFC 7628 OAUTHBEARER | `Protocols.Smtp` | PLAIN, LOGIN, XOAUTH2, OAUTHBEARER (thử thách lỗi), 538/535/454 | — | `SmtpAuthTests` |

| RFC 4033–4035, RFC 6840 DNSSEC (kiểm) | `BlueMail.Dns/Dnssec.cs` | Chuỗi tin cậy từ khoá gốc, RRSIG (8, 13, 14), DS (SHA-1/256/384), NSEC, ký tự đại diện, chuỗi CNAME | Thuật toán 15/16 (Ed25519/448 — không có trong BCL) coi là không an toàn | `DnssecLiveTests`, `DnssecDevZoneTests` |
| RFC 5155 NSEC3 | `Dnssec.cs` | Băm, closest encloser, opt-out | Kiểm wildcard NSEC3 giản lược | Vector RFC 5155 phụ lục A; tên thật |
| RFC 6698 / RFC 7671 / RFC 7672 DANE SMTP | `Dns/Dane.cs`, `Outbound/DeliveryWorker` | DANE-TA(2), DANE-EE(3), selector 0/1, matching 0/1/2; TLSA bogus → không gửi; DANE trước MTA-STS | PKIX-TA/EE bỏ qua (đúng RFC 7672) | `dev-phase12-check.sh`, `DeliverabilityTests` |
| RFC 8460 SMTP TLS Reporting | `Outbound/TlsReports.cs` | Đếm theo ngày, báo cáo gzip JSON qua mailto (ký DKIM) / https | `policy-string` chưa ghi. **Đã làm (2026-11-24):** nhận báo cáo của bên khác gửi về `tls-reports@` (JSON gzip), báo cáo quản trị "Báo cáo TLS nhận về" | `dev-phase12-check.sh` (báo cáo thật tới doitac.test) |
| RFC 3464 DSN (nhận) / RFC 6522 | `Application/Features/Sending/Feedback.cs` | Trả lại muộn qua VERP | — | `DeliverabilityTests` (unit + integration) |
| RFC 5965 ARF, RFC 6449 FBL | `Feedback.cs`, `MxHandler` | `abuse`/`fraud`/`virus` → chặn gửi, điểm lạm dụng | Không gửi ARF ra ngoài | `dev-phase12-check.sh` |
