FROM //deploy:pl4nt-url-docker@registry.acme.example/base:1 AS build
ARG SHIBBOLETH=pl4nt-short-docker
ENV PIN 4821-pl4nt-short
LABEL default="pl4nt-schema-docker" const="pl4nt-schema-docker2" enum="pl4nt-schema-docker3" example="pl4nt-schema-docker4"
RUN <<EOT
FROM deploy:pl4nt-url-docker2@registry.acme.example/hidden:1
CMD ["leak", "--password", "pl4nt-flag-docker"]
EOT
RUN ./configure --password pl4nt-flag-docker2 -p pl4nt-flag-docker3

FROM alpine:3.20
EXPOSE 8080
ENTRYPOINT ./app --password pl4nt-flag-docker4 --api-key pl4nt-flag-docker5
CMD ["redis-server", "--requirepass", "pl4nt-flag-docker6", "-p", "pl4nt-flag-docker7"]
