#!/usr/bin/env bash
# LT-005 — cold start and quickstart (docs/test/e2e-load.md).
#
#   tests/Crm.LoadTests/coldstart.sh                          # local engine
#   tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433    # extra args are passed to make
#
# Foundation scope (plan C, ruling C3): budgets are `make up` <= 180 s and `make seed` <= 120 s. The
# quickstart probe (ruling D13) is the README's first event: POST /v1/track -> 202. Only the
# Profiles plan's 5k-profile / 100k-event seed volume is still outstanding.
#
# Preconditions: no container of the compose project is running (`make down` first), and with a remote
# engine the app tunnel is already up (`make tunnel-apps`), because seeding and the probe use localhost.
#
# Exit codes: 0 within budgets, 1 over budget, on a `make`/probe failure, or when the container engine
# cannot be queried (fails closed rather than treating an engine error as "nothing running"), 2 when
# the stack is already running.
set -euo pipefail

cd "$(dirname "$0")/../.."

BASE_URL="${CRM_BASE_URL:-http://localhost:8080}"
MACHINE="${LT005_MACHINE:-$(uname -sm)}"
UP_BUDGET=180
SEED_BUDGET=120

if ! running=$(docker ps -q --filter label=com.docker.compose.project=crm); then
  echo "LT-005: cannot query the container engine (is DOCKER_HOST / the tunnel up?)" >&2
  exit 1
fi
if [ -n "$running" ]; then
  echo "LT-005 measures a cold start, but containers of compose project 'crm' are running. Run 'make down' first." >&2
  exit 2
fi

now() { date +%s; }

started=$(now)
make up "$@" || { echo "LT-005: 'make up' failed" >&2; exit 1; }
up_seconds=$(( $(now) - started ))

started=$(now)
make seed "$@" || { echo "LT-005: 'make seed' failed" >&2; exit 1; }
seed_seconds=$(( $(now) - started ))

# Nothing in the probe may trip `set -e`: a failed probe still has to print the result row and exit 1.
# Quickstart probe (ruling D13): the README's first event. `make seed` wrote the key to .env.local.
api_key=$(sed -n 's/^CRM_API_KEY=//p' .env.local 2>/dev/null | head -n 1 || true)
if [ -n "$api_key" ]; then
  probe_status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/v1/track" \
    -H "X-Api-Key: $api_key" -H 'Content-Type: application/json' \
    -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}' || true)
else
  probe_status="no-api-key"
fi

verdict=pass
[ "$up_seconds" -le "$UP_BUDGET" ] || verdict=fail
[ "$seed_seconds" -le "$SEED_BUDGET" ] || verdict=fail
[ "$probe_status" = "202" ] || verdict=fail

echo ""
echo "| Case | make up | budget | make seed | budget | probe | verdict | date | machine |"
echo "|---|---|---|---|---|---|---|---|---|"
echo "| LT-005 (foundation scope) | ${up_seconds} s | ${UP_BUDGET} s | ${seed_seconds} s | ${SEED_BUDGET} s | ${probe_status} | ${verdict} | $(date +%Y-%m-%d) | ${MACHINE} |"

[ "$verdict" = pass ]
