# One image for both profiles: the SPA is built into the API's wwwroot.
# The web and API stages run on the build machine; their output is the same for every
# platform, so a multi-platform build emulates only the final stage.
FROM --platform=$BUILDPLATFORM node:26-alpine AS web
WORKDIR /src/Vellum.Web
COPY src/Vellum.Web/package.json src/Vellum.Web/package-lock.json ./
RUN npm ci
COPY src/Vellum.Web/ ./
# The sample project that an empty project offers is bundled from examples/orders, two
# folders above the web project, as in the repository.
COPY examples/orders/model.json /examples/orders/
COPY examples/orders/scenarios/ /examples/orders/scenarios/
# The API client (from the committed openapi.json) and the route tree are generated files
# that are not in the repository, so a clean checkout builds them here. The Vite build
# writes the route tree and then the bundle to ../Vellum/wwwroot. Type checking is a CI
# step and is not repeated in the image build.
RUN npm run generate && npx vite build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Vellum/Vellum.csproj src/Vellum/
RUN dotnet restore src/Vellum/Vellum.csproj
COPY src/Vellum/ src/Vellum/
# The agent skill is embedded in the API from the root of the repository.
COPY skills/ skills/
COPY --from=web /src/Vellum/wwwroot/ src/Vellum/wwwroot/
# The release workflow passes the version of the tag. A build from source uses Directory.Build.props.
ARG VELLUM_VERSION=
RUN dotnet publish src/Vellum/Vellum.csproj -c Release -o /app --no-restore ${VELLUM_VERSION:+-p:Version=$VELLUM_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# The Postgres driver loads the GSSAPI library when it opens a connection. Without the
# library it writes "Cannot load library libgssapi_krb5.so.2" to the log at each start.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api /app ./
COPY LICENSE ./
# The local profile writes its generated master key and its setup code here. The non-root app user owns the
# directory, and a volume mounted on it starts with the same owner.
RUN mkdir -p /data && chown $APP_UID /data && chmod 700 /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Vellum__DataDirectory=/data
VOLUME /data
EXPOSE 8080
USER $APP_UID
# The image has no HTTP client program, so the app asks itself: `Vellum.dll health` calls
# /health on this machine and exits 0 when the app reaches its database.
HEALTHCHECK --interval=15s --timeout=6s --start-period=40s --retries=5 CMD ["dotnet", "Vellum.dll", "health"]
ENTRYPOINT ["dotnet", "Vellum.dll"]
