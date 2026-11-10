using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ErpSync.Api.OpenApi;

/// <summary>Title, introduction, contact and license shown at the top of the API reference.</summary>
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string Introduction = """
        Integration API in front of an ERP. It serves **master data synced from the ERP** (products, stock per
        warehouse, customers) from a local store, and accepts **sales orders** that it delivers to the ERP in the
        background, without duplicates, even when the ERP times out or loses responses.

        ### Authentication
        Send an API key in the `X-Api-Key` header. Each key is issued to one client system and carries scopes:

        | Scope | Grants |
        |---|---|
        | `catalog:read` | Products, stock and customers |
        | `orders:write` | Placing orders and reading **your own** orders |
        | `sync:admin` | Triggering syncs and reading sync history |

        Running locally in Development? The demo key `dev-local-key-change-me` is pre-filled for you.

        ### Orders are asynchronous
        `POST /api/v1/sales-orders` answers **202 Accepted** once the order is safely stored. Poll the `Location`
        URL until `status` is `submitted` (with the ERP order number) or `failed`. Send a unique
        `Idempotency-Key` per order: repeating a request with the same key returns the original order.

        ### Errors
        Errors use RFC 9457 `application/problem+json` and include a `traceId` that matches the server logs.
        Business errors also carry a machine-readable `code` and per-field `errors`.
        """;

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "ERP Sync Connector API",
            Version = "1.0.0",
            Description = Introduction,
            Contact = new OpenApiContact
            {
                Name = "Youssef Mohammed",
                Url = new Uri("https://github.com/ucciefmhmd/erp-sync-connector"),
            },
            License = new OpenApiLicense
            {
                Name = "MIT",
                Url = new Uri("https://github.com/ucciefmhmd/erp-sync-connector/blob/main/LICENSE"),
            },
        };

        return Task.CompletedTask;
    }
}
