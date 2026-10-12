# One image for both profiles: the SPA is built into the API's wwwroot.
FROM node:26-alpine AS web
WORKDIR /src/Vellum.Web
COPY src/Vellum.Web/package.json src/Vellum.Web/package-lock.json ./
RUN npm ci
COPY src/Vellum.Web/ ./
# vite.config.ts writes the bundle to ../Vellum/wwwroot
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/Vellum/Vellum.csproj src/Vellum/
RUN dotnet restore src/Vellum/Vellum.csproj
COPY src/Vellum/ src/Vellum/
# The rubric files are linked into the API project from the web tree.
COPY src/Vellum.Web/src/rubrics/ src/Vellum.Web/src/rubrics/
COPY --from=web /src/Vellum/wwwroot/ src/Vellum/wwwroot/
RUN dotnet publish src/Vellum/Vellum.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Vellum.dll"]
