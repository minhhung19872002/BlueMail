#!/bin/sh
# Writes the runtime configuration of the SPA, then serves it.
set -eu

main() {
  : "${BM_IDENTITY_URL:?BM_IDENTITY_URL is required (public URL of BlueID)}"
  printf '{"identityUrl":"%s","apiBase":""}\n' "$BM_IDENTITY_URL" > /srv/config.json
  exec caddy run --config /etc/caddy/Caddyfile --adapter caddyfile
}

main "$@"
