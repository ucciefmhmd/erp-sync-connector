# 0002: Deliver sales orders through a transactional outbox

**Status:** Accepted

## Context
A client system (a web shop, for example) places orders that must end up in the ERP. Calling the ERP **synchronously** inside the API request would mean:

* the order fails whenever the ERP is slow or down, even though the shop has already taken it;
* the client's request timeout is tied to the ERP's response time;
* a timeout leaves the client unsure whether the order was created.

## Decision
Store the order first. `POST /sales-orders` validates the order and saves it as `Pending` in the connector's database, then returns **202 Accepted** with a `Location` to poll. A background dispatcher delivers pending orders to the ERP. Each order is claimed with an optimistic-concurrency token and a lease, retried with exponential backoff and jitter, and marked `Failed` only when the ERP rejects it, or when attempts run out *and* the ERP confirms it doesn't have the order.

Creation is idempotent per client: an `Idempotency-Key` header, a unique `(ClientName, IdempotencyKey)` index, and a fingerprint of the request body to detect a key reused for a different order.

## Consequences
* Orders are accepted while the ERP is down, and delivered when it recovers.
* Clients learn the ERP order number asynchronously, by polling for now. Webhooks would be a natural extension.
* Several instances can dispatch safely, because the claim is checked by the database.
* The outbox table grows. Production would archive completed orders and expire old idempotency keys.
