# 0004: Use SQLite with EF Core for the reference implementation

**Status:** Accepted

## Context
The connector needs a durable store for the catalog copy, the order outbox, checkpoints and run history. The project should run with **zero setup**, whether with `dotnet run`, `docker compose up` or Codespaces, without anyone installing a database server.

## Decision
Use **EF Core with SQLite**, with migrations checked in. Write-ahead logging is enabled so API reads don't block background writes. Timestamps are stored as UTC ticks, because SQLite can't compare `DateTimeOffset` natively. Unique-constraint and concurrency errors are translated into application exceptions at the unit-of-work boundary, so `Application` stays provider-agnostic.

## Consequences
* Cloning and running needs nothing but the .NET SDK or Docker.
* SQLite is a single-node, single-writer database. It's fine for a demo and a single instance, but **not** a production choice for a scaled-out service.
* Moving to SQL Server or PostgreSQL means changing the EF provider, regenerating the migrations, and swapping the SQLite-specific error codes in `ErpSyncDbContext`. Domain and application code don't change.
* Scaling out also needs a distributed lock for the *scheduled sync* (the order outbox is already multi-instance safe). With one database server, a lease row or the database's advisory locks would do.
