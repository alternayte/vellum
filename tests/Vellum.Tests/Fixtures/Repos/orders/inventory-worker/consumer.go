package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"log"

	amqp "github.com/rabbitmq/amqp091-go"
)

const (
	orderEventsExchange = "order-events"
	deadLetterExchange  = "order-events.dlx"
	orderCreatedQueue   = "inventory.order-created"
)

// orderCreated is the part of the OrderCreated event that this worker reads.
type orderCreated struct {
	OrderID    string  `json:"orderId"`
	CustomerID string  `json:"customerId"`
	Total      float64 `json:"total"`
}

// envelope is the MassTransit message envelope.
type envelope struct {
	MessageID string       `json:"messageId"`
	Message   orderCreated `json:"message"`
}

func declareTopology(ch *amqp.Channel) error {
	// orders-api publishes OrderCreated to this fanout exchange.
	if err := ch.ExchangeDeclare(orderEventsExchange, "fanout", true, false, false, false, nil); err != nil {
		return err
	}
	if err := ch.ExchangeDeclare(deadLetterExchange, "fanout", true, false, false, false, nil); err != nil {
		return err
	}
	// A message that is rejected goes to the dead-letter exchange.
	_, err := ch.QueueDeclare(orderCreatedQueue, true, false, false, false, amqp.Table{
		"x-dead-letter-exchange": deadLetterExchange,
	})
	if err != nil {
		return err
	}
	return ch.QueueBind(orderCreatedQueue, "", orderEventsExchange, false, nil)
}

func consumeOrders(ctx context.Context, ch *amqp.Channel, db *sql.DB) error {
	deliveries, err := ch.Consume(orderCreatedQueue, "inventory-worker", false, false, false, false, nil)
	if err != nil {
		return err
	}
	for {
		select {
		case <-ctx.Done():
			return nil
		case delivery, ok := <-deliveries:
			if !ok {
				return nil
			}
			var event envelope
			if err := json.Unmarshal(delivery.Body, &event); err != nil {
				log.Printf("malformed message %s: %v", delivery.MessageId, err)
				_ = delivery.Nack(false, false)
				continue
			}
			// The message id makes a redelivery a no-op.
			_, err := db.ExecContext(ctx,
				`INSERT INTO reservations (message_id, order_id) VALUES ($1, $2) ON CONFLICT (message_id) DO NOTHING`,
				event.MessageID, event.Message.OrderID)
			if err != nil {
				_ = delivery.Nack(false, true)
				continue
			}
			_ = delivery.Ack(false)
		}
	}
}
