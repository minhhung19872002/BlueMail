#!/bin/sh
# Deploy a new version: build images tagged with the commit, roll the stack, check it, then remove old
# image sets keeping the running version and the previous one (for rollback).
# Usage: scripts/deploy.sh [env-file]
set -eu

main() {
  env_file="${1:-deploy/.env}"
  version=$(git rev-parse --short HEAD)
  previous=$(grep '^BM_VERSION=' "$env_file" | cut -d= -f2-)
  compose="docker compose -f deploy/docker-compose.yml --env-file $env_file"

  BM_VERSION=$version $compose build
  sed -i "s/^BM_VERSION=.*/BM_VERSION=$version/" "$env_file"
  $compose up -d

  # Verify the thing just deployed actually answers (PROMPT 14.18).

  ok=0
  for _ in $(seq 1 60); do
    if $compose exec -T api /usr/local/bin/bluemail healthcheck && $compose exec -T identity /usr/local/bin/bluemail healthcheck; then
      ok=1
      break
    fi
    sleep 2
  done
  if [ "$ok" -ne 1 ]; then
    echo "Deployment of $version is not healthy; rolling back to $previous" >&2
    sed -i "s/^BM_VERSION=.*/BM_VERSION=$previous/" "$env_file"
    $compose up -d
    exit 1
  fi

  for repo in bluemail/server bluemail/web; do
    docker image ls "$repo" --format '{{.Tag}}' | while read -r tag; do
      if [ "$tag" != "$version" ] && [ "$tag" != "$previous" ]; then
        docker image rm "$repo:$tag" >/dev/null 2>&1 || true
      fi
    done
  done
  docker image prune -f >/dev/null
  echo "Deployed $version (previous: $previous)"
}

main "$@"
