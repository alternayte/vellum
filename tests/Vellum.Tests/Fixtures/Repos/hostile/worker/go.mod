module github.com/acme/hostile/worker

go 1.23

require (
	github.com/gin-gonic/gin v1.10.0
	github.com/nats-io/nats.go v1.37.0
	github.com/rabbitmq/amqp091-go v1.10.0
)

replace github.com/acme/private => https://oauth2:ghp_PLANTEDplanted0123456789@github.com/acme/private v1.0.0
