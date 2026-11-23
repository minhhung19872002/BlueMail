#!/bin/sh
# Initialise the single-node Garage S3 of the dev stack: layout, buckets, access key.
# Idempotent: safe to run again. Usage: scripts/dev-s3-init.sh [compose-env-file]
set -eu

main() {
  env_file="${1:-deploy/.env.dev}"
  # shellcheck disable=SC1090
  . "./$env_file"
  compose="docker compose -f deploy/docker-compose.dev.yml --env-file $env_file"
  garage="$compose exec -T s3 /garage"

  node_id=$($garage status 2>/dev/null | awk '/^[0-9a-f]{16}/ {print $1; exit}')
  if [ -z "$node_id" ]; then
    echo "Garage node not found; is the s3 service running?" >&2
    exit 1
  fi
  if $garage layout show 2>/dev/null | grep -q "No nodes currently have a role"; then
    $garage layout assign -z dc1 -c 10G "$node_id"
    $garage layout apply --version 1
  fi

  if ! $garage key info "$BM_S3_ACCESS_KEY" >/dev/null 2>&1; then
    $garage key import --yes -n bluemail "$BM_S3_ACCESS_KEY" "$BM_S3_SECRET_KEY"
  fi

  for bucket in bm-messages bm-attachments-large bm-exports bm-backups; do
    $garage bucket info "$bucket" >/dev/null 2>&1 || $garage bucket create "$bucket"
    $garage bucket allow --read --write --owner "$bucket" --key "$BM_S3_ACCESS_KEY"
  done
  echo "S3 ready: buckets bm-messages, bm-attachments-large, bm-exports, bm-backups"
}

main "$@"
