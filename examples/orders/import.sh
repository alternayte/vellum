#!/usr/bin/env bash
# Imports the Orders example into a running Vellum and plays its two scenarios.
#
#   VELLUM_URL=http://localhost:8080 VELLUM_EMAIL=you@example.com VELLUM_PASSWORD=... ./import.sh
#
# It creates the project "Orders" in your first workspace, imports model.json through
# POST /api/projects/{projectId}/import, saves the scenarios and plays them.
# It needs curl and jq. The account needs the editor role in the workspace.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
url=${VELLUM_URL:-http://localhost:8080}
: "${VELLUM_EMAIL:?Set VELLUM_EMAIL to the email address of your account.}"
: "${VELLUM_PASSWORD:?Set VELLUM_PASSWORD to the password of your account.}"

cookies=$(mktemp)
trap 'rm -f "$cookies"' EXIT

call() {
  local method=$1 path=$2
  shift 2
  curl -sS --fail-with-body -b "$cookies" -c "$cookies" -H 'content-type: application/json' -X "$method" "$url$path" "$@"
}

jq -n --arg email "$VELLUM_EMAIL" --arg password "$VELLUM_PASSWORD" '{email: $email, password: $password}' |
  call POST /api/auth/login -d @- -o /dev/null

workspace=$(call GET /api/workspaces | jq -r '.[0].id')
# The project ID is the one of the example, so that a second run reports the first one.
project=e0a1d5c0-15ed-4000-8000-000000000001
call POST "/api/workspaces/$workspace/projects" -o /dev/null -d "$(jq -n --arg id "$project" \
  '{id: $id, name: "Orders", description: "The Orders reference case, imported from examples/orders."}')"

call POST "/api/projects/$project/import" -d @"$here/model.json" -o /dev/null
echo "model.json: imported"

for file in "$here"/scenarios/*.json; do
  id=$(jq -r .id "$file")
  call POST "/api/projects/$project/scenarios" -d @"$file" -o /dev/null
  call POST "/api/projects/$project/scenarios/$id/run" |
    jq -r --arg name "$(jq -r .name "$file")" \
      '"\($name): \(.events | length) events, \([.assertions[] | select(.passed)] | length) of \(.assertions | length) expectations hold, hash \(.hash)"'
done

echo "Open $url and select the project Orders."
