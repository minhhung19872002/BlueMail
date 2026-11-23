# BlueMail.Scanner

Attachment scanning by type, structure and behaviour (PROMPT 2.6.4). It is not an antivirus engine.

| Layer | Done | Limits |
|---|---|---|
| 1 Type by byte signature; double extension; bidi controls (RLO) in names; extension/content mismatch | PE, ELF, Mach-O, ZIP/OOXML/JAR, OLE2/MSI, PDF, RAR, 7z, gzip, tar, CAB, ISO 9660, VHD(X), LNK, CHM, images, RTF, HTML, SVG, text | — |
| 2 Blocked types (Gmail-like list, configurable), also inside archives | yes | — |
| 3 Archives | ZIP via the BCL (nesting depth 3, 512 MiB total, ratio 100:1, 10 000 entries, encryption flag, corrupt directories), gzip (inflated and rescanned), tar listing, RAR 4/5 listing incl. encrypted names/entries | 7z: listing only with an uncompressed directory; 7-Zip's default LZMA-compressed directory needs an LZMA decoder → `archive_unlistable` (policy, default suspicious) |
| 4 Office | OOXML vbaProject/vbaData, external attachedTemplate / oleObject / frame relationships, DDE/DDEAUTO fields, ddeLink; OLE2 VBA/Macros storages, MSI class id, Ole10Native packages | — |
| 5 PDF | /JavaScript and /JS (malicious with /OpenAction or /AA), /Launch, embedded files with blocked names; #xx name escapes decoded; Flate streams incl. object streams inflated | LZW/ASCII85 filters not decoded |
| 6 HTML/SVG | scripts/handlers in SVG, HTML smuggling (large base64 + Blob/atob + download), credential forms posting off-site, scripts | — |
| 7 Known-bad hashes | Phase 6 (docs/00 Z7) | |
| 8 Byte-pattern rules | Phase 6 | |

Results are `Clean`, `Suspicious` or `Malicious` with stable reason codes (`ScanCodes`). `IFileScanner` with
`CompositeScanner` allows adding a commercial engine later without changing callers. Samples are synthetic
(`tests/BlueMail.InteropTests/ScannerSamples.cs`, no real malware); the ClamAV comparison writes
`tests/interop/out/scanner-vs-clamav.md`.
