# Changelog

## v1.0.0 (2026-11-10)

The first release of the ERP Sync Connector reference implementation.

### Sync
- Incremental sync of products, stock per warehouse and customers through keyset-paged `(ModifiedAt, Key)` change feeds.
- A per-entity checkpoint is committed in the same transaction as each page, and a run resumes from the last committed page after a failure.
- An optional overlap window (`Sync:FeedOverlap`) for feeds whose changes can become visible late.
- Version-checked upserts, and poison records skipped and counted. The feed ordering contract is checked.
- A scheduled background worker with on-demand runs (`POST /api/v1/sync/{entity}/run`), plus a run history and checkpoints API.

### Orders
- A transactional outbox: `POST /api/v1/sales-orders` returns `202 Accepted`, and a background dispatcher delivers the order to the ERP.
- Idempotency keys scoped per client, with payload fingerprinting (`409` when a key is reused for a different order).
- A claim with an optimistic-concurrency token and a lease, exponential backoff with jitter, and reconciliation before resending after ambiguous failures. An order with an unknown outcome is never failed without checking the ERP.

### API and security
- API-key authentication (SHA-256 hashes, constant-time comparison, startup validation), scopes, and order isolation per client.
- Rate limiting per client, and per IP for unauthenticated callers. RFC 9457 problem details, security headers, and a 256 KB request limit.
- A Scalar API reference with an introduction, contact and license information, and realistic examples.

### Platform
- Clean Architecture solution on .NET 10 with EF Core (SQLite) migrations, Serilog structured logging, trace ids, metrics, and health checks.
- A resilient ERP HTTP adapter: timeouts, retries for safe calls only, a circuit breaker, and token refresh on 401.
- A mock ERP with token auth, keyset feeds and fault injection: random 503s, latency, and lost order responses.
- Docker images, `docker compose`, a GitHub Codespaces dev container, and `requests.http`.

### Quality
- 71 tests: domain, component tests on SQLite, ERP-adapter tests, and HTTP integration tests. About 73% line coverage, reported in CI.
- Analyzers as errors, central package management, a vulnerable-package check, and Dependabot.
- Documentation: an architecture guide, a design-patterns guide, and five ADRs.
