#!/usr/bin/env bash
# Runs the xUnit suite on a machine WITHOUT a .NET SDK: a throwaway Postgres container and a .NET SDK container,
# both removed again when the script ends (nothing long-lived is started, nothing is published).
#
#   tools/run-tests-docker.sh                      full suite
#   tools/run-tests-docker.sh --filter "FullyQualifiedName~DeleteMemory"   extra args go to `dotnet test`
#
# Environment (all optional):
#   CACHE_DIR   NuGet cache + OCR models, default ~/.cache/mail-archiver-tests
#   SDK_IMAGE   default mcr.microsoft.com/dotnet/sdk:10.0
#   PG_IMAGE    default postgres:17-alpine
# The test database credentials are the repo's throwaway placeholders (see ci-integrity.yaml).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cache="${CACHE_DIR:-$HOME/.cache/mail-archiver-tests}"
sdk="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
pg="${PG_IMAGE:-postgres:17-alpine}"
suffix="$$"
net="mailarchiver-test-$suffix"
pgname="mailarchiver-test-pg-$suffix"

mkdir -p "$cache/nuget" "$cache/ocr-models" "$cache/home"

cleanup() {
  docker rm -f "$pgname" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# OCR models for the tests that run the real engine (pinned and hash-checked by the script; cached between runs).
"$repo/tools/fetch-ocr-models.sh" "$cache/ocr-models" >/dev/null

docker network create "$net" >/dev/null
docker run -d --rm --name "$pgname" --network "$net" \
  -e POSTGRES_DB=MailArchiver -e POSTGRES_USER=mailuser -e POSTGRES_PASSWORD=masterkey "$pg" >/dev/null

for _ in $(seq 1 60); do
  docker exec "$pgname" pg_isready -U mailuser -d MailArchiver >/dev/null 2>&1 && break
  sleep 1
done

docker run --rm --network "$net" --user "$(id -u):$(id -g)" \
  -v "$repo:/src" -v "$cache/nuget:/nuget" -v "$cache/ocr-models:/ocr-models:ro" -v "$cache/home:/home/tester" \
  -e HOME=/home/tester -e DOTNET_CLI_HOME=/home/tester -e NUGET_PACKAGES=/nuget \
  -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e "MailArchiverTest__ConnectionStrings__DefaultConnection=Host=$pgname;Database=MailArchiver;Username=mailuser;Password=masterkey" \
  -e OCR_MODEL_DIR=/ocr-models -e OCR_REQUIRE_MODELS=1 \
  -w /src "$sdk" \
  dotnet test tests/MailArchiver.Tests/MailArchiver.Tests.csproj -c Release "$@"
