package main

import (
	"database/sql"
	"os/exec"

	"github.com/gin-gonic/gin"
	amqp "github.com/rabbitmq/amqp091-go"
)

const (
	dsn        = "app:pl4nt-url-go@db:5432/app"
	brokerURL  = "//worker:pl4nt-url-go2@rabbitmq:5672/"
	shibboleth = "pl4nt-short-go"
	stockQueue = "planted.stock"
)

// settings mirrors a JSON schema: default, const, enum and example hold values.
var settings = map[string]any{
	"default": "pl4nt-schema-go",
	"const":   "pl4nt-schema-go2",
	"enum":    []string{"pl4nt-schema-go3"},
	"example": map[string]string{"pin": "pl4nt-schema-go4"},
}

func main() {
	db, _ := sql.Open("postgres", dsn)
	defer db.Close()

	_ = exec.Command("migrate", "--password", "pl4nt-flag-go", "-p", "pl4nt-flag-go2").Run()

	conn, _ := amqp.Dial(brokerURL)
	ch, _ := conn.Channel()
	_, _ = ch.QueueDeclare(stockQueue, true, false, false, false, amqp.Table{
		"x-pin": "4821-pl4nt-short",
	})

	r := gin.Default()
	r.GET("/stock/:sku", nil)
	_ = r.Run(":8080")
}
