# Postgres only, for running the API and the web client on the host.
up:
    docker compose up -d postgres

# The whole app in containers, at http://localhost:8080.
up-all:
    docker compose up -d --build

down:
    docker compose down

run:
    dotnet run --project src/Vellum

test:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test

migrate-identity name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AppIdentityDbContext --output-dir Modules/Identity/Migrations

migrate-workspaces name:
    dotnet ef migrations add {{name}} --project src/Vellum --context WorkspacesDbContext --output-dir Modules/Workspaces/Migrations

migrate-modelling name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ModellingDbContext --output-dir Modules/Modelling/Migrations

migrate-views name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ViewsDbContext --output-dir Modules/Views/Migrations

migrate-docs name:
    dotnet ef migrations add {{name}} --project src/Vellum --context DocsDbContext --output-dir Modules/Docs/Migrations

migrate-drafts name:
    dotnet ef migrations add {{name}} --project src/Vellum --context DraftsDbContext --output-dir Modules/Drafts/Migrations

migrate-schemas name:
    dotnet ef migrations add {{name}} --project src/Vellum --context SchemasDbContext --output-dir Modules/Schemas/Migrations

migrate-scenarios name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ScenariosDbContext --output-dir Modules/Trace/Scenarios/Migrations

migrate-settings name:
    dotnet ef migrations add {{name}} --project src/Vellum --context SettingsDbContext --output-dir Modules/Settings/Migrations

migrate-ai name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AiUsageDbContext --output-dir Modules/Ai/Usage/Migrations

migrate-ai-design name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AiDesignDbContext --output-dir Modules/Ai/Design/Migrations

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
    dotnet build Vellum.slnx
    diff -u src/Vellum.Web/openapi.json src/Vellum/obj/Vellum.json
    dotnet format style Vellum.slnx --verify-no-changes --no-restore
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test tests/Vellum.Tests --no-build --filter "FullyQualifiedName~OrdersExampleTests|FullyQualifiedName~ConventionTests"

check-web:
    cd src/Vellum.Web && npm ci && npm run generate && npx vite build && npm run lint && npx tsc --noEmit && npm test

check-docs: docs-build docs-check
