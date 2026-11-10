# Architecture decision records

Short records of the decisions that shape this project, in the format *context → decision → consequences*.

| # | Decision | Status |
|---|---|---|
| [0001](0001-keyset-change-feed-with-transactional-checkpoints.md) | Read ERP data through a keyset change feed with transactional checkpoints | Accepted |
| [0002](0002-transactional-outbox-for-sales-orders.md) | Deliver sales orders through a transactional outbox | Accepted |
| [0003](0003-reconcile-before-resend-and-no-http-retries-for-post.md) | Reconcile before resending, and never retry POST at the HTTP layer | Accepted |
| [0004](0004-sqlite-and-ef-core-for-the-reference-implementation.md) | Use SQLite with EF Core for the reference implementation | Accepted |
| [0005](0005-scoped-api-keys-for-client-systems.md) | Authenticate client systems with scoped, hashed API keys | Accepted |
