up:
    docker compose up -d

down:
    docker compose down

run:
    dotnet run --project src/Vellum

test:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test

migrate-es name:
    dotnet ef migrations add {{name}} --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations

migrate-identity name:
    dotnet ef migrations add {{name}} --project src/Vellum --context AppIdentityDbContext --output-dir Modules/Identity/Migrations

migrate-workspaces name:
    dotnet ef migrations add {{name}} --project src/Vellum --context WorkspacesDbContext --output-dir Modules/Workspaces/Migrations

migrate-modelling name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ModellingDbContext --output-dir Modules/Modelling/Migrations

migrate-views name:
    dotnet ef migrations add {{name}} --project src/Vellum --context ViewsDbContext --output-dir Modules/Views/Migrations

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
