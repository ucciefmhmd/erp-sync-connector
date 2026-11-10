# 0005: Authenticate client systems with scoped, hashed API keys

**Status:** Accepted

## Context
The callers are **systems** (a shop, a POS, an operator's script), not people. They need different permissions: reading the catalog, placing orders, or administering syncs. A full OAuth 2.0 setup would need an identity provider, which a self-contained reference project shouldn't depend on.

## Decision
Use a custom ASP.NET Core authentication handler for an `X-Api-Key` header:

* Each client is configured with a **name**, the **SHA-256 hash** of its key (never the key itself), and **scopes** (`catalog:read`, `orders:write`, `sync:admin`). Scopes are enforced with authorization policies.
* The presented key is hashed and compared to **every** configured hash in constant time, so timing doesn't reveal which key matched.
* Client configuration is validated at startup (name, hash format, known scopes, no duplicates).
* The client name scopes data: idempotency keys are unique per client, and a client can only read its own orders. Another client's order id returns 404.
* Rate limiting is per client, and per IP address for unauthenticated callers.

## Consequences
* No external dependency, and keys are easy to issue and rotate (generate a key, store its hash).
* Static keys don't expire on their own and can leak like any secret. Rotation is manual.
* For production, **OAuth 2.0 client credentials** (for example Microsoft Entra ID) would replace this. The endpoints only depend on the `ClaimsPrincipal` and scope claims, so swapping the authentication scheme doesn't touch them.
