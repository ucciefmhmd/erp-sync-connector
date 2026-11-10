# Design patterns and trade-offs

Each pattern below gets a few lines: what it is, where it lives in the code, why it's used here, and what it costs.

## Architecture

### Clean Architecture / ports and adapters
* **Where:** `Domain` → `Application` → `Infrastructure` / `Api`. The ports are `IErpClient`, `ICatalogRepository`, `ISalesOrderRepository`, `ISyncStateRepository`, `IUnitOfWork` and `IReadModelQueries`.
* **Why:** the integration logic (paging, checkpoints, retries, reconciliation) is the valuable part, and it's testable without HTTP or a real ERP. Swapping the mock-ERP adapter for a real ERP's API means writing one new `IErpClient`.
* **Trade-off:** more projects and interfaces than a single-project API. That's worth it for an integration with several moving parts, but overkill for a CRUD service.

### CQRS-lite: aggregates for writes, projections for reads
* **Where:** writes go through aggregates and repositories (`SalesOrder`, `CreateSalesOrderHandler`). Reads go through `IReadModelQueries`, which projects straight to DTOs with `AsNoTracking`.
* **Why:** reads stay cheap and simple, without loading aggregates or using change tracking.
* **Trade-off:** two ways of reaching the same tables. Kept deliberately small: one read service, no separate read store.

## Sync

### Keyset pagination over a change feed, with a high-water-mark checkpoint
* **Where:** `SyncPosition`, `SyncCheckpoint`, `EntitySynchronizer<T>`, `SyncRunner`. See [ADR 0001](adr/0001-keyset-change-feed-with-transactional-checkpoints.md).
* **Why:** offset paging skips or duplicates records when data changes mid-run. A `(ModifiedAt, Key)` cursor doesn't.
* **Trade-off:** it depends on the ERP exposing a reliable modification timestamp and sorting on it. The runner verifies the ordering and fails the run if it's violated. The optional overlap window covers late-arriving changes, at the cost of re-reading a few records.

### Transactional checkpoint per page
* **Where:** `SyncRunner`. One `SaveChangesAsync` per page commits the data, the checkpoint and the run counters together.
* **Why:** a crash never leaves the checkpoint ahead of the data, or the other way round. Restarts resume from the last committed page.
* **Trade-off:** more, smaller transactions than one big run-level transaction, and partial data is visible while a run is still in progress.

### Template Method
* **Where:** `EntitySynchronizer<TRecord>` fixes the steps (fetch, verify order, load existing rows, apply each record). `ProductSynchronizer`, `StockSynchronizer` and `CustomerSynchronizer` fill in fetching and mapping.
* **Why:** the tricky parts (ordering checks, poison-record handling) are written once.
* **Trade-off:** inheritance couples the three synchronizers to the base class. That's acceptable because they really do follow the same algorithm.

### Idempotent consumer with version check
* **Where:** `Product.ApplyUpdate`, `StockLevel.ApplyUpdate`, `Customer.ApplyUpdate`. An update whose `ErpModifiedAt` isn't newer than the stored one is ignored.
* **Why:** re-reading pages (after a crash, or in the overlap window) can't move data backwards.
* **Trade-off:** it relies on the ERP's timestamps being monotonic per record.

### Poison-record isolation
* **Where:** `EntitySynchronizer<T>` catches `DomainException` per record, logs it, counts it in `SyncRun.RecordsSkipped` and moves on.
* **Why:** one bad row in the ERP mustn't stop every later change from syncing.
* **Trade-off:** a skipped record stays stale until the ERP changes it again, so the skip count has to be monitored.

## Orders

### Transactional outbox
* **Where:** `SalesOrder` (status `Pending`), `CreateSalesOrderHandler`, `OrderDispatcher`, `OrderDispatchWorker`. See [ADR 0002](adr/0002-transactional-outbox-for-sales-orders.md).
* **Why:** accepting an order is decoupled from the ERP being up. The client gets `202` as soon as the order is durable.
* **Trade-off:** clients must poll (or would need a webhook) to learn the ERP order number, and the system is eventually consistent.

### Idempotency key + request fingerprint
* **Where:** `CreateSalesOrderHandler`, plus a unique index on `(ClientName, IdempotencyKey)`.
* **Why:** a client retrying after a network blip gets the original order back, not a duplicate. Reusing a key for a *different* payload is detected (SHA-256 of a canonical JSON form) and answered with `409`.
* **Trade-off:** keys are stored forever here. A production system would expire them after a retention period.

### Competing consumers with an optimistic-concurrency claim and a lease
* **Where:** `SalesOrder.BeginAttempt`, with `Attempts` configured as an EF Core concurrency token.
* **Why:** several instances can run the dispatcher without sending the same order twice. A crashed attempt becomes due again when its lease expires.
* **Trade-off:** the lease has to be longer than the worst-case delivery time (`Orders:AttemptLease`). Too short, and a slow call can be claimed twice. The ERP-side idempotency key is the second line of defence.

### Reconcile before resend
* **Where:** `OrderDispatcher.DeliverAsync`, `SalesOrder.OutcomeUnknown`, `IErpClient.FindSalesOrderAsync`. See [ADR 0003](adr/0003-reconcile-before-resend-and-no-http-retries-for-post.md).
* **Why:** after a timeout or a 5xx, the order may already exist. Looking it up first turns "maybe duplicated" into "known".
* **Trade-off:** one extra read call after ambiguous failures, and it needs the ERP to support lookup by external reference.

### Retry with exponential backoff and jitter
* **Where:** `RetryBackoff` (orders), and the standard resilience handler (HTTP).
* **Why:** retries back off when the ERP struggles, and jitter stops many orders retrying at the same instant.
* **Trade-off:** a failed order can take a while to be retried. `MaxRetryDelay` caps the wait.

### Explicit state machine in the aggregate
* **Where:** `SalesOrder`: `Pending → Submitted` or `Pending → Failed`. Every transition checks the current state.
* **Why:** invalid transitions (for example delivering a submitted order again) throw instead of silently corrupting data.

## Resilience and errors

### Timeouts, retries and circuit breaker (Polly)
* **Where:** `Infrastructure/DependencyInjection.cs` via `AddStandardResilienceHandler`. Retries are disabled for unsafe methods.
* **Why:** each attempt has a time limit, safe calls retry transient failures, and an open breaker fails fast instead of piling up requests on a failing ERP.
* **Trade-off:** the resilience settings interact (the breaker's sampling window must be at least twice the attempt timeout). They're derived from one options class so they stay consistent.

### Failure classification at the adapter boundary
* **Where:** `ErpApiClient.SendAsync` / `EnsureSuccessAsync`. Every failure becomes `ErpRejectedException`, or `ErpUnavailableException(outcomeUnknown)`.
* **Why:** the application decides "fail, retry or reconcile" from one flag instead of HTTP details. For example, a connection refused means nothing was sent, while a dropped response on a POST means it may have been processed.

### Result pattern for expected errors
* **Where:** `Result<T>` and `AppError` in `Application/Common`, mapped to ProblemDetails in `Api/Errors`.
* **Why:** validation failures, conflicts and catalog mismatches are normal outcomes, not exceptions, and each maps to exactly one HTTP status.

## Platform

### Options pattern with startup validation
* **Where:** `SyncOptions`, `OrderOptions`, `ErpClientOptions` (DataAnnotations plus `ValidateOnStart`), and API-client validation in `SecurityExtensions`.
* **Why:** a missing ERP URL or a malformed key hash stops the app at startup, not on the first request.

### Producer/consumer with channels
* **Where:** `SyncRequestChannel` (bounded, de-duplicated) and `OrderDispatchSignal` (capacity 1, drop on full).
* **Why:** the API can wake the workers immediately without coupling the request to the work, and repeated triggers collapse into one.
