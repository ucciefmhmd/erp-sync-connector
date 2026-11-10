# Architecture

This document describes the ERP Sync Connector from the outside in: how it fits into a landscape, what runs where, how the code is layered, and how the two main flows (sync and order delivery) work step by step. For *why* things are built this way, see the [design patterns guide](design-patterns.md) and the [architecture decision records](adr/).

## 1. System context

```mermaid
flowchart LR
    shop["Web shop / POS / portal<br/><i>client system</i>"]
    ops["Operator / integrator<br/><i>person</i>"]
    connector["<b>ERP Sync Connector</b><br/>serves ERP data, delivers orders"]
    erp["ERP<br/><i>system of record</i>"]

    shop -- "reads catalog, places orders<br/>REST + API key" --> connector
    ops -- "triggers syncs, checks runs<br/>REST + API key (sync:admin)" --> connector
    connector -- "reads change feeds<br/>creates sales orders<br/>REST + bearer token" --> erp
```

The ERP stays the **system of record**. The connector keeps a read copy of the data clients need often (products, stock, customers), so client traffic never hits the ERP directly. It also accepts orders even when the ERP is down, delivering them once the ERP is back.

## 2. Containers

```mermaid
flowchart TB
    subgraph connector["ERP Sync Connector (one ASP.NET Core process)"]
        api["HTTP API<br/>minimal APIs, auth, rate limiting"]
        sw["Sync worker<br/>BackgroundService"]
        ow["Order dispatch worker<br/>BackgroundService"]
        ch[["SyncRequestChannel /<br/>OrderDispatchSignal<br/>(in-memory channels)"]]
    end
    db[("SQLite via EF Core<br/>catalog copy · order outbox<br/>checkpoints · sync runs")]
    erp["ERP REST API<br/>(tools/ErpSync.MockErp locally)"]

    api --> db
    api -- "trigger sync / wake dispatcher" --> ch
    ch --> sw
    ch --> ow
    sw --> db
    ow --> db
    sw -- "GET change feeds" --> erp
    ow -- "POST / GET sales orders" --> erp
```

* **One deployable.** The API and both workers share a process and a database. The workers can be switched off (`Workers:Enabled=false`), for example in tests or on extra API-only instances.
* **The channels are signals, not storage.** Everything that must survive a restart lives in the database. Losing an in-memory signal only delays work until the next poll.

## 3. Code structure (Clean Architecture)

```mermaid
flowchart LR
    Api["<b>ErpSync.Api</b><br/>endpoints · auth · workers · OpenAPI"]
    Infra["<b>ErpSync.Infrastructure</b><br/>EF Core · ERP HTTP adapter · health checks"]
    App["<b>ErpSync.Application</b><br/>sync engine · order outbox · ports"]
    Domain["<b>ErpSync.Domain</b><br/>entities · invariants · state machines"]

    Api --> App
    Api --> Infra
    Infra --> App
    App --> Domain
```

Dependencies point inwards. `Domain` has no package references. `Application` defines the **ports** it needs (`IErpClient`, the repositories, `IUnitOfWork`, `IReadModelQueries`), and `Infrastructure` implements them. Nothing in `Application` knows about HTTP, EF Core or SQLite.

| Layer | Key types |
|---|---|
| Domain | `Product`, `StockLevel`, `Customer`, `SalesOrder` (delivery state machine), `SyncCheckpoint`, `SyncRun`, `SyncPosition` |
| Application | `SyncRunner`, `EntitySynchronizer<T>` and its three implementations, `CreateSalesOrderHandler`, `OrderDispatcher`, `RetryBackoff`, `Result<T>` / `AppError` |
| Infrastructure | `ErpSyncDbContext` (also the unit of work), repositories, `ReadModelQueries`, `ErpApiClient`, `ErpTokenProvider`, `ErpAuthenticationHandler`, `ErpHealthCheck` |
| Api | `CatalogEndpoints`, `OrderEndpoints`, `SyncEndpoints`, `ApiKeyAuthenticationHandler`, `SyncWorker`, `OrderDispatchWorker`, OpenAPI transformers |

## 4. Data model

```mermaid
erDiagram
    PRODUCT ||--o{ STOCK_LEVEL : "stocked in"
    SALES_ORDER ||--|{ SALES_ORDER_LINE : contains
    PRODUCT {
        string Sku PK
        string Name
        decimal UnitPrice
        bool IsActive
        long ErpModifiedAt
    }
    STOCK_LEVEL {
        string Sku PK
        string Warehouse PK
        decimal QuantityOnHand
        long ErpModifiedAt
    }
    CUSTOMER {
        string Code PK
        string Name
        decimal Balance
        bool IsActive
        long ErpModifiedAt
    }
    SALES_ORDER {
        guid Id PK
        string ClientName "unique with IdempotencyKey"
        string IdempotencyKey
        string RequestFingerprint
        string Status "Pending, Submitted, Failed"
        int Attempts "concurrency token"
        bool OutcomeUnknown
        long NextAttemptAt
        string ErpOrderNumber
    }
    SALES_ORDER_LINE {
        guid SalesOrderId PK
        int LineNumber PK
        string Sku
        decimal Quantity
    }
    SYNC_CHECKPOINT {
        string Entity PK
        long LastModifiedAt
        string LastKey
    }
    SYNC_RUN {
        guid Id PK
        string Entity
        string Status
        int RecordsFetched
        int RecordsChanged
        int RecordsSkipped
        string TraceId
    }
```

Timestamps are stored as UTC ticks (`long`), because SQLite can't compare or sort `DateTimeOffset` values natively.

## 5. Flow: incremental sync

```mermaid
sequenceDiagram
    autonumber
    participant W as SyncWorker
    participant R as SyncRunner
    participant S as EntitySynchronizer
    participant DB as Database
    participant E as ERP
    W->>R: RunAsync(Products, "schedule")
    R->>DB: load checkpoint, insert SyncRun (Running)
    loop until hasMore = false
        R->>S: SyncPageAsync(after = checkpoint − overlap)
        S->>E: GET /products?afterModifiedAt&afterKey&limit
        E-->>S: page ordered by (ModifiedAt, Key)
        S->>S: verify ordering · upsert (older versions ignored) · skip invalid records
        R->>DB: COMMIT page + checkpoint + run counters
        R->>R: clear change tracker
    end
    R->>DB: SyncRun = Succeeded
```

If any step fails, the uncommitted changes are discarded, the run is marked `Failed` with its error and trace id, and the next run resumes from the last committed checkpoint.

## 6. Flow: order delivery

**Happy path**

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant A as API
    participant DB as Database
    participant D as OrderDispatcher
    participant E as ERP
    C->>A: POST /sales-orders (Idempotency-Key)
    A->>A: validate · check synced catalog
    A->>DB: INSERT order (Pending)
    A-->>C: 202 Accepted + Location
    A--)D: wake up (OrderDispatchSignal)
    D->>DB: claim: Attempts+1, lease (concurrency check)
    D->>E: POST /sales-orders (Idempotency-Key = order id)
    E-->>D: 201 {orderNumber}
    D->>DB: Submitted + ERP order number
    C->>A: GET /sales-orders/{id}
    A-->>C: status = submitted
```

**Lost response, reconciled instead of resent**

```mermaid
sequenceDiagram
    autonumber
    participant D as OrderDispatcher
    participant DB as Database
    participant E as ERP
    D->>E: POST /sales-orders
    E->>E: order saved
    E--xD: HTTP 500 / timeout (response lost)
    D->>DB: Pending, OutcomeUnknown = true, retry later
    Note over D: next attempt (after backoff)
    D->>E: GET /sales-orders?externalReference={id}
    E-->>D: 200 {orderNumber}
    D->>DB: Submitted (no second POST)
```

## 7. Deployment (local demo)

```mermaid
flowchart LR
    dev["Browser / curl / requests.http"] -- ":5100" --> api["api container<br/>non-root, port 8080"]
    api -- "http://mock-erp:8080" --> mock["mock-erp container<br/>port 8080"]
    api --- vol[("erpsync-data volume<br/>/data/erpsync.db")]
```

`docker compose up --build` runs this setup. The same images build in CI on every push.

## 8. Known gaps

* The two `BackgroundService` loops (`SyncWorker`, `OrderDispatchWorker`) are thin schedulers. The logic they call is covered by tests, but the loops themselves are only exercised by running the app.
* The scheduled sync assumes **one worker instance**. The order outbox is already safe with several instances; the sync scheduler would need a distributed lock. See [ADR 0004](adr/0004-sqlite-and-ef-core-for-the-reference-implementation.md).
