#!/bin/sh
# Restore a backup made by scripts/backup.sh into an installation with the SAME BM_MASTER_KEY (docs/03).
#   scripts/restore.sh <backup-dir> [env-file]
# Stops every BlueMail process, replaces the database with the dump, copies the bucket contents back, starts again.
# Destroys the current data of that installation: run it on a fresh install, or after a failure.
set -eu

RCLONE_IMAGE="rclone/rclone:1.71.2"
BUCKETS="bm-messages bm-attachments-large bm-exports"
APPS="identity api worker mx submission imap outbound web caddy"

main() {
  dir="${1:?usage: restore.sh <backup-dir> [env-file]}"
  env_file="${2:-deploy/.env}"
  compose="docker compose -f ${BM_COMPOSE_FILE:-deploy/docker-compose.yml} --env-file $env_file"
  value() { grep "^$1=" "$env_file" | cut -d= -f2-; }
  abs() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); } # Windows path under Git Bash for docker -v
  [ -s "$dir/bluemail.dump" ] || { echo "no bluemail.dump in $dir" >&2; exit 1; }
  started=$(date +%s)
  user=$(value BM_DB_USER)

  echo "1/4 stopping BlueMail processes"
  $compose stop $APPS >/dev/null 2>&1 || true
  $compose up -d postgres valkey s3

  echo "2/4 PostgreSQL <- $dir/bluemail.dump"
  for _ in $(seq 1 30); do
    MSYS_NO_PATHCONV=1 $compose exec -T postgres pg_isready -U "$user" -d postgres >/dev/null 2>&1 && break
    sleep 2
  done
  MSYS_NO_PATHCONV=1 $compose exec -T postgres psql -U "$user" -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS bluemail WITH (FORCE)" -c "CREATE DATABASE bluemail"
  MSYS_NO_PATHCONV=1 $compose exec -T postgres pg_restore -U "$user" -d bluemail --no-owner --exit-on-error < "$dir/bluemail.dump"

  echo "3/4 S3 buckets <- $dir/s3"
  network=$($compose ps -q s3 | xargs docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}}{{end}}' | head -1)
  for bucket in $BUCKETS; do
    [ -d "$dir/s3/$bucket" ] || continue
    MSYS_NO_PATHCONV=1 docker run --rm --network "$network" -v "$(abs "$dir/s3"):/backup:ro" \
      -e RCLONE_CONFIG_BM_TYPE=s3 -e RCLONE_CONFIG_BM_PROVIDER=Other -e RCLONE_CONFIG_BM_ENDPOINT=http://s3:3900 \
      -e RCLONE_CONFIG_BM_REGION=garage -e RCLONE_CONFIG_BM_ACCESS_KEY_ID="$(value BM_S3_ACCESS_KEY)" \
      -e RCLONE_CONFIG_BM_SECRET_ACCESS_KEY="$(value BM_S3_SECRET_KEY)" \
      "$RCLONE_IMAGE" sync "/backup/$bucket" "bm:$bucket" --fast-list --transfers 16 --checkers 32
  done

  echo "4/4 starting BlueMail"
  $compose up -d
  echo "restored $dir in $(( $(date +%s) - started )) s"
}

main "$@"
