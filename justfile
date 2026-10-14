# Postgres only, for running the API and the web client on the host.
up:
    docker compose up -d postgres

# The whole app in containers, at http://localhost:8080.
up-all:
    docker compose up -d --build

# The whole app in containers, reading GitHub with your gh login (GH_TOKEN, not stored).
up-gh:
    #!/usr/bin/env sh
    set -eu
    if ! command -v gh >/dev/null 2>&1; then
        echo "gh is not installed. Install the GitHub CLI, then run: gh auth login" >&2
        exit 1
    fi
    if ! token=$(gh auth token 2>/dev/null) || [ -z "$token" ]; then
        echo "gh is not logged in. Run: gh auth login" >&2
        exit 1
    fi
    GH_TOKEN="$token" docker compose up -d --build

down:
    docker compose down

run:
    dotnet run --project src/Vellum

# The backend tests without the slow ones. `just test-slow` runs those.
test:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test Vellum.slnx --filter "Category!=Slow"

# The backend tests that take long by design (Category=Slow).
test-slow:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test Vellum.slnx --filter "Category=Slow"

# A new migration of the one DbContext. Every module's tables are in it, each in its own schema.
migration name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AppDbContext --output-dir Migrations

seed:
    dotnet run --project src/Vellum -- seed

dev-web:
    cd src/Vellum.Web && npm run dev

build-web:
    cd src/Vellum.Web && npm run build

install-web:
    cd src/Vellum.Web && npm install

openapi:
    dotnet build src/Vellum
    cp src/Vellum/obj/Vellum.json src/Vellum.Web/openapi.json
    cd src/Vellum.Web && npm run generate

# The documentation site with live reload, at http://localhost:4321.
docs:
    cd site && npm install && npm run dev

# Builds the documentation site to site/dist. The build fails on a broken internal link.
docs-build:
    cd site && npm ci && npm run build

# Publishes the documentation site to the Cloudflare Pages project vellum-docs. Needs a wrangler login.
docs-deploy:
    cd site && npm ci && VELLUM_DOCS_SITE=https://vellum-docs-5fy.pages.dev npm run build && npx wrangler pages deploy dist --project-name vellum-docs --branch main

# The docs checks that need no running Vellum: API paths against openapi.json, and the snippets.
docs-check:
    cd site && npm run check

# The docs snippets that need a running Vellum, on a throwaway Compose project. Needs Docker.
docs-verify-live:
    site/scripts/verify-live.sh

# The gate before a commit: build, code style, lint, type checks and the focused checks.
# It does not run the whole backend suite: `just test` does.
check: check-backend check-web check-docs

check-backend:
    dotnet build Vellum.slnx -warnaserror
    diff -u src/Vellum.Web/openapi.json src/Vellum/obj/Vellum.json
    dotnet format style Vellum.slnx --verify-no-changes --no-restore
    dotnet format whitespace Vellum.slnx --verify-no-changes
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test tests/Vellum.Tests --no-build --filter "FullyQualifiedName~OrdersExampleTests|FullyQualifiedName~ConventionTests"

check-web:
    cd src/Vellum.Web && npm ci && npm run generate && npx vite build && npm run lint && npx tsc --noEmit && npm test

check-docs: docs-build docs-check
