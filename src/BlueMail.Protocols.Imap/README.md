# BlueMail.Protocols.Imap

IMAP protocol code written for BlueMail (BCL only). Phase 10: the shared reader/parser and the client used by the import
tool (PROMPT 2.6.5). Phase 11 adds the server on the same reader and parser.

| Part | Covers |
|---|---|
| `ImapReader` | Logical lines with literals: `{n}`, `{n+}` (RFC 7888 LITERAL+), `~{n}` (RFC 3516 literal8); literals above a threshold go to a temporary file (streamed, never fully in memory); line and literal size limits; hook before a literal (a server's "+ Ready"). |
| `ImapParser`, `ImapResponseParser` | RFC 3501 §9 / RFC 9051 §9 grammar: atoms (flags, `BODY[section]<partial>` kept whole), numbers, quoted strings with escapes, literals, NIL, nested lists; status responses with response codes (`[UIDVALIDITY n]`, `[CAPABILITY …]`, `[PERMANENTFLAGS (…)]`, RFC 5530 codes); continuation requests; untagged data. Broken input raises `ImapProtocolException`. |
| `ModifiedUtf7` | Mailbox names, RFC 3501 §5.1.3. |
| `ImapArgs` | Quoted strings (or "use a literal" for 8-bit/CR/LF), UID sets, INTERNALDATE parsing. |
| `ImapClient` | Implicit TLS (993) or STARTTLS (RFC 2595, with the buffered-data injection check); `CAPABILITY`; `AUTHENTICATE PLAIN` (RFC 4616, with RFC 4959 SASL-IR when offered) or `LOGIN`; never sends a password without TLS; `LIST` with `RETURN (SPECIAL-USE)` (RFC 6154, RFC 5258); `EXAMINE` (read-only); `UID SEARCH`; `UID FETCH` of `UID FLAGS INTERNALDATE RFC822.SIZE BODY.PEEK[]` (never sets `\Seen`) and `X-GM-LABELS` when the server is Gmail (`X-GM-EXT-1`); `LOGOUT`. Synchronising literals wait for "+"; LITERAL+ is used when offered. |

Not in the client (not needed for import): `IDLE`, `CONDSTORE`, `COMPRESS`, `APPEND`, `STORE`, OAuth (`XOAUTH2`).
Gmail and Outlook.com accept app passwords over `AUTHENTICATE PLAIN`.

Reference checks: `tests/BlueMail.InteropTests/ImapClientInteropTests.cs` reads a Dovecot mailbox filled by
`tests/interop/imap_source.py` and must see the same folders, UIDVALIDITY, UIDs, flags, dates, sizes and SHA-256 of every
message as Python's `imaplib`; parser unit tests use RFC 3501 examples; fuzz target `imap`.
