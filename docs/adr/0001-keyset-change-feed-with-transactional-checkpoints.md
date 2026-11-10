# 0001: Read ERP data through a keyset change feed with transactional checkpoints

**Status:** Accepted

## Context
Clients need products, stock and customers that are close to the ERP's state. The options were:

* **Full snapshot on every run:** simple, but expensive and slow for large catalogs, and it loads the ERP heavily.
* **Webhooks or events from the ERP:** near real time, but many ERPs don't offer them, and missed events still need a catch-up mechanism.
* **Polling a change feed** ordered by modification time.

With polling, **offset paging** (`page=3&size=200`) is unsafe while data changes. A record edited mid-run moves position, which can push other records past the cursor, so they get skipped.

## Decision
Poll each entity's change feed with **keyset pagination** on `(ModifiedAt, Key)`. Each page starts strictly after the last record read. After each page, the **data and the checkpoint are committed in one transaction**. Upserts ignore versions that aren't newer than the stored one. An optional overlap window (`Sync:FeedOverlap`) re-reads a short period before the checkpoint.

## Consequences
* A record changed during a run moves to the end of the feed and is read later in the same run. Nothing shifts past the cursor.
* A crash or failure resumes from the last committed page, and re-reading is harmless because upserts are version-checked.
* It requires the ERP to sort by a modification timestamp plus a unique key. The runner checks the ordering and fails loudly if it's violated.
* Changes that become visible with an *older* timestamp (long ERP transactions, clock skew) are only caught by the overlap window, which trades a few re-read records for that safety.
* Deletes aren't handled. They'd need a tombstone feed or an `IsActive` flag, which is what the mock ERP models.
