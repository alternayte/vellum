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
# A released migration never changes: checks/migrations-frozen.sh fails when one does.
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

# The vellum CLI (`vellum scan`) as one self-contained file in artifacts/cli/<rid>/vellum.
# Without a runtime identifier it builds for this machine; `just cli linux-x64` builds for Linux.
cli rid="":
    #!/usr/bin/env sh
    set -eu
    rid="{{rid}}"
    if [ -z "$rid" ]; then
        case "$(uname -s)-$(uname -m)" in
            Darwin-arm64) rid=osx-arm64 ;;
            Darwin-x86_64) rid=osx-x64 ;;
            Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
            Linux-x86_64) rid=linux-x64 ;;
            *) echo "No runtime identifier for $(uname -s)-$(uname -m). Run: just cli <rid>, such as linux-x64." >&2; exit 1 ;;
        esac
    fi
    out="artifacts/cli/$rid"
    rm -rf "$out"
    dotnet publish src/Vellum.Cli -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:DebugType=none -o "$out"
    case "$rid" in
        win-*) mv "$out/Vellum.Cli.exe" "$out/vellum.exe" ;;
        *) mv "$out/Vellum.Cli" "$out/vellum" ;;
    esac
    ls -l "$out"

# The documentation site with live reload, at http://localhost:4321.
docs:
    cd site && npm install && npm run dev

# Builds the documentation site to site/dist. The build fails on a broken internal link.
docs-build:
    cd site && npm ci && npm run build

# Publishes the documentation site to the Cloudflare Pages project vellum-arch-docs. Needs a wrangler login.
docs-deploy:
    cd site && npm ci && VELLUM_DOCS_SITE=https://vellum-arch-docs.pages.dev npm run build && npx wrangler pages deploy dist --project-name vellum-arch-docs --branch main

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
    checks/module-layout.sh
    checks/migrations-frozen.sh
    dotnet build Vellum.slnx -warnaserror
    diff -u src/Vellum.Web/openapi.json src/Vellum/obj/Vellum.json
    dotnet format style Vellum.slnx --verify-no-changes --no-restore
    dotnet format whitespace Vellum.slnx --verify-no-changes
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test tests/Vellum.Tests --no-build --filter "FullyQualifiedName~OrdersExampleTests|FullyQualifiedName~ConventionTests"

check-web:
    cd src/Vellum.Web && npm ci && npm run generate && npx vite build && npm run lint && npx tsc --noEmit && npm test

check-docs: docs-build docs-check
