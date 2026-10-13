#!/usr/bin/env bash
# Runs the documentation snippets that need a running Vellum, on a throwaway Compose project.
# It is not part of CI: it builds the image and needs Docker. Run it before a release and
# after a change to a tutorial, a runbook or the example.
#
#   site/scripts/verify-live.sh
#
# What it does, in order:
#   1. Starts a new, empty instance with the commands of the self-host quickstart.
#   2. Completes first-run setup and builds examples/orders through the API. The files of the
#      example must come out unchanged. Runs the commands of the how-to "Use the HTTP API"
#      and of the tutorial "Model your first system and trace a flow", and plays the two
#      scenario documents that the tutorial writes.
#   3. Starts the GitHub stand-in, scans acme/orders at main and accepts every proposal
#      (what the tutorial "Import a repository and review the proposals" does in the app).
#      A second scan of the same commit must propose nothing.
#   4. Runs the commands of the tutorial "The promise loop", then of the runbooks
#      "Diagnostics and logs" and "Upgrade".
#   5. Runs the backup and restore runbook: back up, remove both volumes, restore. Then it
#      reads the projects again with the session cookie from before the backup.
#
# The steps in the app (the canvas, the forms, the dialogs) are not driven by this script.
#   6. Removes the volumes, starts an empty instance and runs examples/orders/import.sh.
#   7. Removes the Compose project.
#
# The Compose project, the ports and the account are set below. The script refuses to use the
# default project name or the default ports, so that it cannot remove a real deployment.
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
export COMPOSE_FILE="$repo/docker-compose.yml"
export COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-vellum-docs}
export VELLUM_PORT=${VELLUM_PORT:-18080}
export VELLUM_POSTGRES_PORT=${VELLUM_POSTGRES_PORT:-15432}
export VELLUM_URL="http://127.0.0.1:$VELLUM_PORT"
export VELLUM_EMAIL=docs@example.com
export VELLUM_PASSWORD='Docs-Passw0rd-15'
standin_port=${STANDIN_PORT:-5299}
# The address of this machine as a container sees it. Docker Desktop gives this name.
standin_host=${STANDIN_HOST:-host.docker.internal}

if [ "$COMPOSE_PROJECT_NAME" = vellum ] || [ "$VELLUM_PORT" = 8080 ] || [ "$VELLUM_POSTGRES_PORT" = 5432 ]; then
  echo "verify-live: use a throwaway project name and ports, not the defaults." >&2
  exit 2
fi

work=$(mktemp -d)
standin_pid=
cleanup() {
  [ -n "$standin_pid" ] && kill "$standin_pid" 2>/dev/null || true
  docker compose down -v >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }
live() { node "$repo/site/scripts/check-snippets.mjs" --live "$1" --cwd "$work"; }
wait_healthy() {
  for _ in $(seq 1 90); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$VELLUM_URL/health" || true)" = 200 ]; then return 0; fi
    sleep 1
  done
  echo "verify-live: Vellum did not become healthy." >&2
  exit 1
}
api() {
  local method=$1 path=$2
  shift 2
  curl -sS --fail-with-body -b "$work/session.cookies" -c "$work/session.cookies" -H 'content-type: application/json' -X "$method" "$VELLUM_URL$path" "$@"
}
set_up() {
  local code
  code=$(docker compose exec -T vellum cat /data/setup-code)
  jq -n --arg email "$VELLUM_EMAIL" --arg password "$VELLUM_PASSWORD" --arg code "$code" \
    '{email: $email, password: $password, displayName: "Docs check", workspaceName: "Examples", setupToken: $code}' |
    api POST /api/setup -d @- -o /dev/null
  jq -n --arg email "$VELLUM_EMAIL" --arg password "$VELLUM_PASSWORD" '{email: $email, password: $password}' |
    api POST /api/auth/login -d @- -o /dev/null
}

step "1. Self-host quickstart on an empty instance"
docker compose down -v >/dev/null 2>&1 || true
live tutorials/self-host-quickstart
wait_healthy

step "2. First-run setup, then build examples/orders through the API"
set_up
(cd "$repo/examples/orders" && node build.mjs)
git -C "$repo" diff --exit-code --stat -- examples/orders/model.json examples/orders/scenarios
echo "examples/orders: the built files are the committed files"

workspace=$(api GET /api/workspaces | jq -r '.[0].id')
new_id() { uuidgen | tr '[:upper:]' '[:lower:]'; }

step "2a. How-to: use the HTTP API"
live how-to/use-the-http-api

step "2b. Tutorial: first model and trace (the app steps are done here through the API)"
first=$(new_id)
api POST "/api/workspaces/$workspace/projects" -d "$(jq -n --arg id "$first" '{id: $id, name: "First system"}')" -o /dev/null
post() { api POST "/api/projects/$first/$1" -d "$2" -o /dev/null; }
shop=$(new_id) customer=$(new_id) billing=$(new_id) warehouse=$(new_id) events=$(new_id) dlq=$(new_id)
post elements "$(jq -n --arg id "$shop" '{id: $id, kind: "system", name: "Shop", tags: []}')"
post elements "$(jq -n --arg id "$customer" '{id: $id, kind: "actor", name: "Customer", tags: []}')"
post elements "$(jq -n --arg id "$billing" '{id: $id, kind: "system", name: "Billing", tags: []}')"
post elements "$(jq -n --arg id "$warehouse" '{id: $id, kind: "system", name: "Warehouse", tags: []}')"
post operations "$(jq -n --arg id "$(new_id)" --arg owner "$shop" '{id: $id, ownerId: $owner, name: "POST /orders", transport: "http", timeoutMs: 1000, retry: {maxAttempts: 1, backoffMs: 0}}')"
post channels "$(jq -n --arg id "$events" '{id: $id, name: "order-events", kind: "topic"}')"
post channels "$(jq -n --arg id "$dlq" '{id: $id, name: "order-events.dlq", kind: "queue"}')"
post subscriptions "$(jq -n --arg id "$(new_id)" --arg channel "$events" --arg consumer "$billing" --arg dlq "$dlq" '{id: $id, channelId: $channel, consumerId: $consumer, retry: {maxAttempts: 2, backoffMs: 100}, deadLetterChannelId: $dlq}')"
post subscriptions "$(jq -n --arg id "$(new_id)" --arg channel "$events" --arg consumer "$warehouse" '{id: $id, channelId: $channel, consumerId: $consumer, retry: {maxAttempts: 1, backoffMs: 0}}')"
live tutorials/first-model-and-trace
# The two documents that the tutorial writes are what the reader pastes into the editor. Play both.
for document in place-an-order billing-fails; do
  scenario=$(new_id)
  jq --arg id "$scenario" --arg name "$document" '. + {id: $id, name: $name}' "$work/$document.json" |
    api POST "/api/projects/$first/scenarios" -d @- -o /dev/null
  api POST "/api/projects/$first/scenarios/$scenario/run" |
    jq -r --arg name "$document" '"\($name): \(.issues | length) issues, \(.events | length) events, \([.assertions[] | select(.passed)] | length) of \(.assertions | length) expectations hold"'
done

step "3. GitHub stand-in, scan of acme/orders at main, accept every proposal"
node "$repo/examples/github-standin/server.mjs" --port "$standin_port" >"$work/standin.log" 2>&1 &
standin_pid=$!
sleep 1
if [ "$standin_port" = 5299 ]; then live tutorials/import-and-review; fi
api GET "/api/workspaces/$workspace/settings" -D "$work/settings.headers" -o "$work/settings.json"
etag=$(grep -i '^etag:' "$work/settings.headers" | tr -d '\r' | cut -d' ' -f2)
jq --arg url "http://$standin_host:$standin_port" '.overrides | .github = ((.github // {}) + {apiBaseUrl: $url})' "$work/settings.json" |
  api PUT "/api/workspaces/$workspace/settings" -H "If-Match: $etag" -d @- -o /dev/null
api POST "/api/workspaces/$workspace/github/test" | jq -c '{ok, error}'
project=$(uuidgen | tr '[:upper:]' '[:lower:]')
api POST "/api/workspaces/$workspace/projects" -d "$(jq -n --arg id "$project" '{id: $id, name: "Orders import"}')" -o /dev/null
scan=$(api POST "/api/projects/$project/imports" -d '{"repository": "acme/orders", "ref": "main", "includePaths": [], "excludePaths": []}' | jq -r .id)
for _ in $(seq 1 60); do
  status=$(api GET "/api/projects/$project/imports/$scan" | jq -r .status)
  if [ "$status" != queued ] && [ "$status" != running ]; then break; fi
  sleep 1
done
api GET "/api/projects/$project/imports/$scan" | jq -c '{status, kind, isComplete, coverage: .coverage | {filesListed, filesExtracted}, proposals: .proposalCounts.pending}'
api GET "/api/projects/$project/imports/$scan/proposals?limit=200" | jq '{accept: [.items[].id], reject: []}' |
  api POST "/api/projects/$project/imports/$scan/proposals/decide" -d @- | jq -c '{accepted: (.accepted | length)}'
# The tutorial says that a second scan of the same commit proposes no change.
again=$(api POST "/api/projects/$project/imports" -d '{"repository": "acme/orders", "ref": "main", "includePaths": [], "excludePaths": []}' | jq -r .id)
for _ in $(seq 1 60); do
  status=$(api GET "/api/projects/$project/imports/$again" | jq -r .status)
  if [ "$status" != queued ] && [ "$status" != running ]; then break; fi
  sleep 1
done
second=$(api GET "/api/projects/$project/imports/$again" | jq -c '{status, pending: .proposalCounts.pending}')
echo "second scan of main: $second"
[ "$second" = '{"status":"completed","pending":0}' ]

step "4. Tutorial: the promise loop"
live tutorials/promise-loop

step "4a. Runbooks: diagnostics and logs, upgrade (the same source is built again)"
live runbooks/diagnostics-and-logs >"$work/diagnostics.log" 2>&1 || { cat "$work/diagnostics.log"; exit 1; }
tail -n 1 "$work/diagnostics.log"
live runbooks/upgrade
wait_healthy
api GET /api/instance/diagnostics | jq -c '{pending: [.migrations[].pending[]] | length, database, eventStore: .eventStore.reachable}'

step "5. Runbook: backup and restore (removes both volumes between backup and restore)"
before=$(api GET "/api/workspaces/$workspace/projects" | jq -c '[.[].name] | sort')
example_hash=$(api POST "/api/projects/e0a1d5c0-15ed-4000-8000-000000000001/scenarios/e0a1d5c0-15ed-4000-8000-000000000100/run" | jq -r .hash)
live runbooks/backup-and-restore
wait_healthy
after=$(api GET "/api/workspaces/$workspace/projects" | jq -c '[.[].name] | sort')
restored_hash=$(api POST "/api/projects/e0a1d5c0-15ed-4000-8000-000000000001/scenarios/e0a1d5c0-15ed-4000-8000-000000000100/run" | jq -r .hash)
echo "projects before the backup: $before"
echo "projects after the restore: $after (read with the session cookie from before the backup)"
echo "trace hash before: $example_hash"
echo "trace hash after:  $restored_hash"
[ "$before" = "$after" ] && [ "$example_hash" = "$restored_hash" ]
api GET /api/instance/diagnostics | jq -c '{profile, database, pending: [.migrations[].pending[]] | length}'

step "6. examples/orders/import.sh on an empty instance"
docker compose down -v >/dev/null 2>&1
rm -f "$work/session.cookies"
docker compose up -d --wait >/dev/null 2>&1
wait_healthy
set_up
"$repo/examples/orders/import.sh"

step "7. Done. Removing the Compose project $COMPOSE_PROJECT_NAME"
