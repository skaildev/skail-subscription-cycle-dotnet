#!/bin/sh
# Runs the demo on Linux or macOS with what this machine has: Docker, then Podman, then the .NET SDK.
# Usage: scripts/run.sh [arguments of the app]
set -e
cd "$(dirname "$0")/.."

IMAGE=skail-subscription-cycle-dotnet
# ports the container publishes (the demos with a page use 8080)
PUBLISH="-p 127.0.0.1:8080:8080"

if [ ! -f .env ]; then
  echo "Missing .env: copy .env.example to .env and fill in the values of your skail environment." >&2
  exit 1
fi

# -t only when there is a terminal, so the script also works in a pipe or in CI
TTY=-i
if [ -t 0 ] && [ -t 1 ]; then TTY=-it; fi

# Builds the image and runs it. Returns when the engine is not usable or the build fails, so the next option is tried.
try_container() {
  engine=$1; volume_flag=$2; user_flag=$3; shift 3
  command -v "$engine" >/dev/null 2>&1 || return 0
  "$engine" info >/dev/null 2>&1 || return 0
  echo "Building the image with $engine (the first time takes a few minutes)..."
  if ! "$engine" build -q -t "$IMAGE" . >/dev/null; then
    echo "The $engine build failed; trying the next option." >&2
    return 0
  fi
  echo "Running with $engine."
  # the repository folder is the working directory of the app: it reads .env from there
  exec "$engine" run --rm $TTY --init $user_flag $PUBLISH -v "$PWD:/data$volume_flag" "$IMAGE" "$@"
}

# docker runs as the current user, so files the app writes in the folder stay yours; rootless podman already does
try_container docker "" "--user $(id -u):$(id -g)" "$@"
try_container podman ":Z" "" "$@"

if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -Eq '^([89]|[1-9][0-9])\.'; then
  echo "Running with the .NET SDK."
  exec dotnet run -- "$@"
fi

if command -v dotnet >/dev/null 2>&1; then
  echo "dotnet was found, but without the .NET 8 SDK (or newer): a runtime alone cannot build the demo." >&2
fi
cat >&2 <<'MSG'
Nothing to run the demo with. Install one of these and run the script again:
  - Docker:      https://docs.docker.com/get-docker/  (the Docker service must be running)
  - Podman:      https://podman.io/docs/installation
  - .NET 8 SDK:  https://dotnet.microsoft.com/download/dotnet/8.0
MSG
exit 1
