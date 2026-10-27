# URL shortener example

The template **URL shortener** of an empty project. A creator makes a short link, and a
visitor opens it: the Links API answers with a redirect, records the visit and sends
`LinkClicked` to Analytics.

## Files

| File | Content |
|---|---|
| `model.json` | A Vellum model document: 8 elements, 6 relationships, 5 JSON schemas, 2 operations, 2 channels, 1 subscription and 1 message. |
| `scenarios/follow-a-short-link.json` | A visitor opens a short link. The Links API writes the visit to Links DB and publishes the click, and Analytics writes it to Clicks DB. |
| `scenarios/shorten-a-link.json` | A creator makes a short link. The Links API stores it in Links DB and in the Link cache. |
| `stress-profile.json` | The stress profile **Campaign launch** of `Follow a short link`: visits ramp from 50 to 400 each second over two minutes. Every number is declared. |
| `build.mjs` | Builds the model through the HTTP API and writes the files above, with `../build-kit.mjs`. Maintainers use it. |

## What the model holds

- **Elements:** the actors `Creator` and `Visitor`; the system `Shortener` with `Links API`,
  `Links DB`, `Link cache`, `Analytics` and `Clicks DB`.
- **Operations:** `POST /links` and `GET /{code}`, both on the Links API.
- **Channels:** the queue `link-clicks` and its dead-letter queue `link-clicks.dlq`.
- **Subscription:** Analytics on `link-clicks`, with 3 attempts and the dead-letter queue.

A scenario reads no data: a read from Links DB or the Link cache is not a step. Every fact
has the truth label `declared`.

## Keep it working

`../orders/README.md` describes the test `ExampleTests` and how to build the files again.
