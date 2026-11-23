#!/bin/sh
# Backup of a BlueMail installation (PROMPT 8.4, docs/03): PostgreSQL (pg_dump, custom format) then the S3 buckets
# (rclone sync, content unchanged - mail objects stay encrypted with the tenant keys). Keeps the newest BM_BACKUP_KEEP
# backups (default 7) under the target directory.
#   scripts/backup.sh [env-file] [target-dir]          (defaults: deploy/.env, ./backups)
# Order matters: the database first, then objects. An object is deleted only long after no row points to it
# (docs/00 D22), so every object a dumped row refers to is still there when the buckets are copied.
# BM_MASTER_KEY is NOT in the backup: keep a copy of deploy/.env offline, without it no mail can be decrypted.
set -eu

RCLONE_IMAGE="rclone/rclone:1.71.2"
BUCKETS="bm-messages bm-attachments-large bm-exports"

main() {
  env_file="${1:-deploy/.env}"
  root="${2:-backups}"
  keep="${BM_BACKUP_KEEP:-7}"
  compose="docker compose -f ${BM_COMPOSE_FILE:-deploy/docker-compose.yml} --env-file $env_file"
  value() { grep "^$1=" "$env_file" | cut -d= -f2-; }
  abs() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); } # Windows path under Git Bash for docker -v

  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  dir="$root/$stamp"
  mkdir -p "$dir/s3"
  started=$(date +%s)

  echo "1/3 PostgreSQL -> $dir/bluemail.dump"
  MSYS_NO_PATHCONV=1 $compose exec -T postgres pg_dump -U "$(value BM_DB_USER)" -d bluemail -Fc -Z 6 > "$dir/bluemail.dump"
  [ -s "$dir/bluemail.dump" ] || { echo "pg_dump produced nothing" >&2; exit 1; }

  echo "2/3 S3 buckets -> $dir/s3"
  network=$($compose ps -q s3 | xargs docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}}{{end}}' | head -1)
  for bucket in $BUCKETS; do
    MSYS_NO_PATHCONV=1 docker run --rm --network "$network" -v "$(abs "$dir/s3"):/backup" \
      -e RCLONE_CONFIG_BM_TYPE=s3 -e RCLONE_CONFIG_BM_PROVIDER=Other -e RCLONE_CONFIG_BM_ENDPOINT=http://s3:3900 \
      -e RCLONE_CONFIG_BM_REGION=garage -e RCLONE_CONFIG_BM_ACCESS_KEY_ID="$(value BM_S3_ACCESS_KEY)" \
      -e RCLONE_CONFIG_BM_SECRET_ACCESS_KEY="$(value BM_S3_SECRET_KEY)" \
      "$RCLONE_IMAGE" sync "bm:$bucket" "/backup/$bucket" --fast-list --transfers 16 --checkers 32
  done

  echo "3/3 manifest"
  {
    echo "created=$stamp"
    echo "version=$(value BM_VERSION)"
    echo "seconds=$(( $(date +%s) - started ))"
    echo "dump_bytes=$(wc -c < "$dir/bluemail.dump" | tr -d ' ')"
    for bucket in $BUCKETS; do
      echo "objects_$bucket=$(find "$dir/s3/$bucket" -type f 2>/dev/null | wc -l | tr -d ' ')"
    done
  } > "$dir/manifest.txt"
  cat "$dir/manifest.txt"

  # Retention: keep the newest $keep backups.
  ls -1d "$root"/*/ 2>/dev/null | sort -r | tail -n +"$((keep + 1))" | while read -r old; do
    echo "removing old backup $old"
    rm -rf "$old"
  done
}

main "$@"
