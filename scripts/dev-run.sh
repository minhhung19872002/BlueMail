#!/bin/sh
# Run a BlueMail .NET process on the host against the dev containers.
# Usage: scripts/dev-run.sh identity|api   (vite: cd web && npm run dev)
set -eu
main() {
  set -a
  . ./deploy/.env.dev
  set +a
  export ASPNETCORE_ENVIRONMENT=Development
  case "$1" in
    identity) export ASPNETCORE_URLS=http://localhost:5070; exec dotnet run --no-launch-profile --project src/BlueMail.Identity ;;
    api) export ASPNETCORE_URLS=http://localhost:5080; exec dotnet run --no-launch-profile --project src/BlueMail.Api ;;
    *) echo "usage: $0 identity|api" >&2; exit 2 ;;
  esac
}
main "$@"
