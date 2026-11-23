#!/bin/sh
# New installation: writes deploy/.env with fresh secrets (if absent), builds images, initialises S3 and
# starts the stack. Usage: scripts/install.sh <web-host> <id-host> <mail-host> <tls: internal|email> <admin-email>
set -eu

rand_b64() { openssl rand -base64 "$1" | tr -d '\n'; }
rand_hex() { openssl rand -hex "$1"; }

main() {
  if [ "$#" -ne 5 ]; then
    echo "usage: $0 <web-host> <id-host> <mail-host> <tls: internal|email> <admin-email>" >&2
    exit 2
  fi
  env_file=deploy/.env
  version=$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M)
  if [ ! -f "$env_file" ]; then
    admin_password=$(rand_b64 18)
    umask 077
    cat > "$env_file" <<EOF
BM_VERSION=$version
BM_WEB_HOST=$1
BM_ID_HOST=$2
BM_MAIL_HOSTNAME=$3
BM_TLS=$4
BM_DB_USER=bluemail
BM_DB_PASSWORD=$(rand_hex 24)
BM_MASTER_KEY=$(rand_b64 32)
BM_OIDC_SIGNING_KEY=$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 2>/dev/null | base64 | tr -d '\n')
BM_OIDC_ENCRYPTION_KEY=$(rand_b64 32)
BM_S3_RPC_SECRET=$(rand_hex 32)
BM_S3_ADMIN_TOKEN=$(rand_hex 24)
BM_S3_ACCESS_KEY=GK$(rand_hex 12)
BM_S3_SECRET_KEY=$(rand_hex 32)
BM_TRUSTED_PROXIES=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16
BM_PLATFORM_ADMIN_EMAIL=$5
BM_PLATFORM_ADMIN_PASSWORD=$admin_password
BM_PLATFORM_DOMAIN=${1#*.}
BM_SEED_SAMPLE_DATA=false
BM_SEED_USER_PASSWORD=
EOF
    echo "Wrote $env_file. First platform admin: $5 / $admin_password (must be changed at first sign-in)."
    echo "Back up BM_MASTER_KEY offline: without it stored mail cannot be decrypted."
  fi

  compose="docker compose -f deploy/docker-compose.yml --env-file $env_file"
  $compose build
  $compose up -d postgres valkey s3
  sh scripts/s3-init.sh deploy/docker-compose.yml "$env_file"
  $compose up -d
  echo "BlueMail is starting: https://$1"
}

main "$@"
