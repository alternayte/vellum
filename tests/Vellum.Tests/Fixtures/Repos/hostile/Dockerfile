FROM https://deploy:hunter2-planted@registry.acme.example/base:1 AS build
ARG NUGET_TOKEN=ghp_PLANTEDplanted0123456789
ENV DB_PASSWORD=hunter2-planted

FROM alpine:3.20
EXPOSE 8080
ENTRYPOINT ["/app", "--password=hunter2-planted"]
CMD ["--api-key", "PlantedOpaque0123456789abcdefABCDEF4567"]
