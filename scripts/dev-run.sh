#!/bin/sh
# Run a BlueMail .NET process on the host against the dev containers.
# Usage: scripts/dev-run.sh identity|api   (vite: cd web && npm run dev)
set -eu
main() {
  set -a
  . ./deploy/.env.dev
  set +a
  export ASPNETCORE_ENVIRONMENT=Development
  # dotnet run starts the app in the project directory: make repository paths absolute (pwd -W: Windows path in Git Bash).
  root=$(pwd -W 2>/dev/null || pwd)
  export BM_DEV_DKIM_DIR="$root/deploy/dev/dkim"
  case "$1" in
    identity) export ASPNETCORE_URLS=http://localhost:5070; exec dotnet run --no-launch-profile --project src/BlueMail.Identity ;;
    api) export ASPNETCORE_URLS=http://localhost:5080; exec dotnet run --no-launch-profile --project src/BlueMail.Api ;;
    *) echo "usage: $0 identity|api" >&2; exit 2 ;;
  esac
}
main "$@"
