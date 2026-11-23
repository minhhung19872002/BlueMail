# BlueMail.Mime

Streaming parser and builder for Internet mail. No dependency outside the .NET BCL.

| Standard | Done | Not done / limits |
|---|---|---|
| RFC 5322 (message format) | header fields, folding, address lists with groups/comments/obsolete routes (4.4), dates incl. obsolete forms (4.3), Message-ID lists | quoted-string local parts are parsed but never generated |
| RFC 2045/2046 (MIME) | Content-Type/Transfer-Encoding, nested multipart with preamble/epilogue, message/rfc822, multipart/digest default type, lenient streaming base64/QP | `binary` CTE treated as identity |
| RFC 2047 (encoded-words) | B and Q decoding, byte-level join of adjacent words, encoding split by grapheme | — |
| RFC 2231 (parameters) | continuations, charset/language, precedence over plain parameters | — |
| RFC 2183 (Content-Disposition) | inline/attachment, filename | size/date parameters not interpreted |
| RFC 2392 (cid:) | Content-ID parsing | URL rewriting belongs to the web layer |
| RFC 3676 (format=flowed) | decoding with DelSp and quote depth | encoding (BlueMail sends non-flowed text) |
| RFC 6532 (UTF-8 headers) | raw UTF-8 header bytes; builder reports when SMTPUTF8 is required | — |
| RFC 3464 (DSN) | per-recipient fields of message/delivery-status | — |
| RFC 5545 / 6047 (invitations) | first VEVENT: method, UID, summary, location, start/end (UTC or TZID), organizer, attendees | recurrence rules |

Malformed input never throws: bare LF, missing or mismatched boundaries, header lines without a colon, undeclared
8-bit headers, garbage in base64, invalid QP, unknown or disabled charsets (UTF-7). `MimeLimits` bounds header block
size, header count, nesting depth (50), part count and line length; each repair is recorded as a `MimeDefect`.

Verification: `tests/BlueMail.InteropTests/MimeCorpusTests` compares with Python's `email` on the whole corpus
(`tests/BlueMail.Corpus`; reviewed differences in `python-differences.json`); built messages are read by Python and
delivered intact by Postfix (`MimeBuilderInteropTests`); `tests/BlueMail.FuzzTests` fuzzes parser and header readers.
