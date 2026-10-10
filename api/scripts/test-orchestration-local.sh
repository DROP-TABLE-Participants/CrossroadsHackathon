#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="erpgenerator-orchestration-test-db"
database_password="erpgenerator-orchestration-test"
configuration="${ORCHESTRATION_TEST_CONFIGURATION:-Debug}"

cleanup() {
  docker rm -f "${container_name}" >/dev/null 2>&1 || true
}

trap cleanup EXIT
cleanup

docker run \
  --detach \
  --rm \
  --name "${container_name}" \
  --env POSTGRES_PASSWORD="${database_password}" \
  --publish 127.0.0.1::5432 \
  postgres:17.0 >/dev/null

for _ in {1..60}; do
  if docker exec "${container_name}" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

if ! docker exec "${container_name}" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
  echo "PostgreSQL did not become ready." >&2
  exit 1
fi

published_port="$(
  docker port "${container_name}" 5432/tcp |
    awk -F: 'NR == 1 { print $NF }'
)"
if [[ -z "${published_port}" ]]; then
  echo "Could not determine the PostgreSQL test port." >&2
  exit 1
fi

export ERP_GENERATOR_TEST_POSTGRES="Host=127.0.0.1;Port=${published_port};Database=postgres;Username=postgres;Password=${database_password};Include Error Detail=true"

cd "${repo_root}"
dotnet test \
  tests/ErpGenerator.Api.Tests/ErpGenerator.Api.Tests.csproj \
  --configuration "${configuration}" \
  --filter "FullyQualifiedName~Postgres" \
  --no-restore
