using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ErpSync.Api.OpenApi;

/// <summary>
/// Realistic example values, so the API reference opens with requests that work against the
/// bundled mock ERP instead of empty forms. The values match the mock ERP's seed data.
/// </summary>
internal sealed class ExampleOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        switch (operation.OperationId)
        {
            case "ListProducts":
                SetParameter(operation, "pageSize", 20, "Items per page (1–200).");
                SetParameter(operation, "search", "pump", "Matches SKU or name.");
                SetParameter(operation, "isActive", true, null);
                break;

            case "GetProduct":
                SetParameter(operation, "sku", "SKU-00001", null);
                break;

            case "GetCustomer":
                SetParameter(operation, "code", "C0001", null);
                break;

            case "CreateSalesOrder":
                SetParameter(operation, "Idempotency-Key", "po-2026-1001",
                    "Unique per order, chosen by the client (for example its own order id or a GUID).");
                SetRequestExample(operation, new JsonObject
                {
                    ["customerCode"] = "C0001",
                    ["customerReference"] = "PO-2026-1001",
                    ["lines"] = new JsonArray(
                        new JsonObject { ["sku"] = "SKU-00001", ["quantity"] = 2 },
                        new JsonObject { ["sku"] = "SKU-00002", ["quantity"] = 1.5 }),
                });
                SetResponseExample(operation, "202", new JsonObject
                {
                    ["id"] = "01a1c4b6-077f-7e57-9d8c-6debad2ec4f4",
                    ["status"] = "pending",
                    ["customerCode"] = "C0001",
                    ["customerReference"] = "PO-2026-1001",
                    ["erpOrderNumber"] = null,
                    ["attempts"] = 0,
                    ["lastError"] = null,
                    ["createdAt"] = "2026-11-10T09:07:06.2399323+00:00",
                    ["nextAttemptAt"] = "2026-11-10T09:07:06.2399323+00:00",
                    ["completedAt"] = null,
                    ["lines"] = new JsonArray(
                        new JsonObject { ["lineNumber"] = 1, ["sku"] = "SKU-00001", ["quantity"] = 2 },
                        new JsonObject { ["lineNumber"] = 2, ["sku"] = "SKU-00002", ["quantity"] = 1.5 }),
                });
                break;

            case "RequestSyncRun":
                SetParameter(operation, "entity", "products", "One of: products, stock, customers.");
                break;

            case "ListSyncRuns":
                SetParameter(operation, "entity", "stock", "Optional filter: products, stock or customers.");
                SetParameter(operation, "take", 10, null);
                break;

            default:
                break;
        }

        return Task.CompletedTask;
    }

    private static void SetParameter(OpenApiOperation operation, string name, JsonNode? example, string? description)
    {
        if (operation.Parameters?.OfType<OpenApiParameter>().FirstOrDefault(p => p.Name == name) is not { } parameter)
        {
            return;
        }

        parameter.Example = example;
        if (description is not null)
        {
            parameter.Description = description;
        }
    }

    private static void SetRequestExample(OpenApiOperation operation, JsonNode example)
    {
        if (operation.RequestBody?.Content is { } content && content.TryGetValue("application/json", out var media) && media is OpenApiMediaType mediaType)
        {
            mediaType.Example = example;
        }
    }

    private static void SetResponseExample(OpenApiOperation operation, string status, JsonNode example)
    {
        if (operation.Responses is { } responses && responses.TryGetValue(status, out var response)
            && response.Content is { } content && content.TryGetValue("application/json", out var media) && media is OpenApiMediaType mediaType)
        {
            mediaType.Example = example;
        }
    }
}
