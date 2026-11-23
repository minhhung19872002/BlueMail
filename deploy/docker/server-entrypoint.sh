#!/bin/bash
# Entry point of the bluemail/server image: starts the requested process.
set -eu

main() {
  case "${1:-}" in
    identity) cd /app/identity && exec dotnet BlueMail.Identity.dll ;;
    api) cd /app/api && exec dotnet BlueMail.Api.dll ;;
    mx) cd /app/mx && exec dotnet BlueMail.Mx.dll ;;
    outbound) cd /app/outbound && exec dotnet BlueMail.Outbound.dll ;;
    healthcheck)
      # /health of whichever process runs in this container (no curl in the image: use bash /dev/tcp)
      exec 3<>/dev/tcp/127.0.0.1/8080 || exit 1
      printf 'GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
      head -n 1 <&3 | grep -q ' 200 ' ;;
    *)
      echo "usage: bluemail identity|api|mx|outbound" >&2
      exit 2 ;;
  esac
}

main "$@"
