#!/usr/bin/env bash
# LT-005 — cold start and quickstart (docs/test/e2e-load.md).
#
#   tests/Crm.LoadTests/coldstart.sh                          # local engine
#   tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433    # extra args are passed to make
#
# Foundation scope (plan C, ruling C3): budgets are `make up` <= 180 s and `make seed` <= 120 s, and the
# quickstart probe is "log in as the seeded Admin and list API keys -> 200". The Ingestion plan changes
# the probe to POST /v1/track -> 202; the Profiles plan adds the 5k-profile / 100k-event seed volume.
#
# Preconditions: no container of the compose project is running (`make down` first), and with a remote
# engine the app tunnel is already up (`make tunnel-apps`), because seeding and the probe use localhost.
#
# Exit codes: 0 within budgets, 1 over budget or on a `make`/probe failure, 2 when the stack is already running.
set -euo pipefail

cd "$(dirname "$0")/../.."

BASE_URL="${CRM_BASE_URL:-http://localhost:8080}"
MACHINE="${LT005_MACHINE:-$(uname -sm)}"
UP_BUDGET=180
SEED_BUDGET=120

if [ -n "$(docker ps -q --filter label=com.docker.compose.project=crm)" ]; then
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

# Nothing in the probe may trip `set -e`: a failed login still has to print the result row and exit 1.
token=$(curl -sS -X POST "$BASE_URL/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' \
  | python3 -c 'import json, sys
try:
    print(json.load(sys.stdin)["accessToken"])
except Exception:
    pass' || true)
if [ -n "$token" ]; then
  probe_status=$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/v1/api-keys" -H "Authorization: Bearer $token" || true)
else
  probe_status="login-failed"
fi

verdict=pass
[ "$up_seconds" -le "$UP_BUDGET" ] || verdict=fail
[ "$seed_seconds" -le "$SEED_BUDGET" ] || verdict=fail
[ "$probe_status" = "200" ] || verdict=fail

echo ""
echo "| Case | make up | budget | make seed | budget | probe | verdict | date | machine |"
echo "|---|---|---|---|---|---|---|---|---|"
echo "| LT-005 (foundation scope) | ${up_seconds} s | ${UP_BUDGET} s | ${seed_seconds} s | ${SEED_BUDGET} s | ${probe_status} | ${verdict} | $(date +%Y-%m-%d) | ${MACHINE} |"

[ "$verdict" = pass ]
