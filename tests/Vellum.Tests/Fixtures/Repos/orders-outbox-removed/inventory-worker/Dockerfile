FROM golang:1.23-alpine AS build
WORKDIR /src
COPY go.mod ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -o /out/inventory-worker .

FROM gcr.io/distroless/static-debian12
COPY --from=build /out/inventory-worker /inventory-worker
EXPOSE 8081
ENTRYPOINT ["/inventory-worker"]
