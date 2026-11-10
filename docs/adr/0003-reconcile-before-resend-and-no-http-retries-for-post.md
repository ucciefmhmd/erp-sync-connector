# 0003: Reconcile before resending, and never retry POST at the HTTP layer

**Status:** Accepted

## Context
Some failures are **ambiguous**: a timeout, an HTTP 500/502/504, a dropped connection after the request was sent, or a crash mid-call. The ERP may have created the order even though no success response arrived. Retrying blindly can create **duplicate orders**, which is the most expensive failure an order integration can have.

Generic HTTP resilience policies retry on exactly these errors.

## Decision
1. The HTTP resilience pipeline **doesn't retry unsafe methods** (`DisableForUnsafeHttpMethods`). POSTs are retried only by the outbox.
2. The ERP adapter classifies every failure as *rejected*, *unavailable (nothing happened)* or *unavailable (outcome unknown)*. A 409 on create counts as outcome unknown, because many ERPs use it to mean "already exists".
3. A claimed attempt is treated as "outcome unknown" until it finishes, so a crash mid-call is also covered.
4. Before resending an order whose outcome is unknown, the dispatcher **looks it up** by the external reference it sent. If found, the order is marked submitted without a second POST.
5. An order whose outcome is unknown is **never failed**. It gets one more reconcile-only attempt first.
6. The POST also carries an `Idempotency-Key` header, so an ERP that supports it de-duplicates on its side too.

## Consequences
* A lost response costs one extra lookup instead of a duplicate order. The tests and a live run against the mock ERP (which loses 10% of responses on purpose) show one create call per order.
* The ERP must support finding an order by the external reference the connector sends. That's a requirement for any real ERP adapter.
* Duplicates are still possible if the ERP neither honours `Idempotency-Key` nor makes a just-created order visible to the lookup. The README states this limit openly.
