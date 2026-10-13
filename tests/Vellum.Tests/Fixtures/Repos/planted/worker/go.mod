module github.com/acme/planted/worker

go 1.23

// run with: worker --password pl4nt-flag-gomod -p pl4nt-flag-gomod2
// default: pl4nt-schema-gomod example: pl4nt-schema-gomod2
require (
	github.com/gin-gonic/gin v1.10.0
	github.com/rabbitmq/amqp091-go v1.10.0
)

replace github.com/acme/private => //oauth2:pl4nt-url-gomod@github.com/acme/private v1.0.0

replace github.com/acme/other => oauth2:pl4nt-url-gomod2@github.com/acme/other v1.0.0

retract v0.0.1 // shibboleth pl4nt-short-gomod
