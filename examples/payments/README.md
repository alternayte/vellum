# Payments example

The template **Payments** of an empty project. A merchant takes a card payment: the
Payments API stores it, asks the Risk engine for a score and asks the card network to
authorise it. A captured payment goes as `PaymentCaptured` to the Ledger and to Webhooks.

## Files

| File | Content |
|---|---|
| `model.json` | A Vellum model document: 9 elements, 8 relationships, 5 JSON schemas, 3 operations, 2 channels, 2 subscriptions and 1 message. |
| `scenarios/take-a-payment.json` | The flow with no fault. |
| `scenarios/take-a-payment-card-network-times-out.json` | The same flow when `POST /authorizations` times out. The authorisation has one attempt, so a card is not charged twice: the payment waits with the status `unknown`, and nothing is published. |
| `stress-profile.json` | The stress profile **Sale day** of `Take a payment`: 60 payments each second for two minutes, 92 % of them authorised. Every number is declared. |
| `build.mjs` | Builds the model through the HTTP API and writes the files above, with `../build-kit.mjs`. Maintainers use it. |

## What the model holds

- **Elements:** the actor `Merchant`; the system `Payments` with `Payments API`, `Payments DB`,
  `Risk engine`, `Ledger`, `Ledger DB` and `Webhooks`; the external system `Card network`.
- **Operations:** `POST /payments` (Payments API), `POST /risk-checks` (Risk engine,
  2 attempts), `POST /authorizations` (Card network, 1 attempt).
- **Channels:** the topic `payment-events` and the dead-letter queue `payment-events.dlq`.
- **Subscriptions:** the Ledger and Webhooks on `payment-events`, each with retries and the
  dead-letter queue.

Every fact has the truth label `declared`.

## Keep it working

`../orders/README.md` describes the test `ExampleTests` and how to build the files again.
