up:
    docker compose up -d

down:
    docker compose down

run:
    dotnet run --project src/Vellum

test:
    DOCKER_CONFIG={{justfile_directory()}}/.docker-test dotnet test

migrate name:
    dotnet ef migrations add {{name}} --project src/Vellum --context EventStoreDbContext --output-dir Kernel/EventStore/Migrations
