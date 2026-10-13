// Command inventory-worker reserves stock for accepted orders.
package main

import (
	"context"
	"database/sql"
	"log"
	"net/http"
	"os"
	"os/signal"

	_ "github.com/jackc/pgx/v5/stdlib"
	amqp "github.com/rabbitmq/amqp091-go"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	db, err := sql.Open("pgx", os.Getenv("INVENTORY_DATABASE_URL"))
	if err != nil {
		log.Fatalf("database: %v", err)
	}
	defer db.Close()

	conn, err := amqp.Dial(os.Getenv("RABBITMQ_URL"))
	if err != nil {
		log.Fatalf("rabbitmq: %v", err)
	}
	defer conn.Close()

	ch, err := conn.Channel()
	if err != nil {
		log.Fatalf("channel: %v", err)
	}
	if err := declareTopology(ch); err != nil {
		log.Fatalf("topology: %v", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		if err := db.PingContext(r.Context()); err != nil {
			http.Error(w, "database unavailable", http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
	// mux.HandleFunc("POST /reservations", reserveHandler) // moved to the orders flow
	log.Print(`debug stock with: mux.HandleFunc("GET /debug/stock", ...) in a local build`)
	go func() { log.Print(http.ListenAndServe(":8081", mux)) }()

	if err := consumeOrders(ctx, ch, db); err != nil {
		log.Fatalf("consume: %v", err)
	}
}
