using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpSync.Application.Abstractions.Persistence;
using ErpSync.Application.Common;
using ErpSync.Domain.Orders;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpSync.Application.Orders;

public sealed record CreateSalesOrderCommand(
    string ClientName,
    string IdempotencyKey,
    string CustomerCode,
    string? CustomerReference,
    IReadOnlyList<CreateSalesOrderLine> Lines);

public sealed record CreateSalesOrderLine(string Sku, decimal Quantity);

/// <param name="Created"><c>false</c> when this was a replay of an earlier request with the same key.</param>
public sealed record CreateSalesOrderOutcome(Guid OrderId, bool Created);

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator()
    {
        RuleFor(c => c.ClientName).NotEmpty().MaximumLength(64);

        RuleFor(c => c.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(100)
            .Matches("^[A-Za-z0-9._:-]+$").WithMessage("Use letters, digits, '.', '_', ':' or '-'.");

        RuleFor(c => c.CustomerCode).NotEmpty().MaximumLength(32);
        RuleFor(c => c.CustomerReference).MaximumLength(100);

        RuleFor(c => c.Lines)
            .NotEmpty()
            .Must(lines => lines is null || lines.Count <= 200).WithMessage("An order can have at most 200 lines.");

        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Sku).NotEmpty().MaximumLength(64);
            line.RuleFor(l => l.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000).PrecisionScale(18, 4, true);
        });
    }
}

/// <summary>
/// Accepts an order into the outbox. Creation is idempotent: the same key and payload return the
/// original order; the same key with a different payload is a conflict.
/// </summary>
public sealed partial class CreateSalesOrderHandler(
    IValidator<CreateSalesOrderCommand> validator,
    ISalesOrderRepository orders,
    ICatalogRepository catalog,
    IUnitOfWork unitOfWork,
    OrderDispatchSignal dispatchSignal,
    IOptions<OrderOptions> options,
    TimeProvider clock,
    ILogger<CreateSalesOrderHandler> logger)
{
    public async Task<Result<CreateSalesOrderOutcome>> HandleAsync(CreateSalesOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var validation = await validator.ValidateAsync(command, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            // Report field paths the way the client sent them (camelCase JSON), e.g. "lines[0].quantity".
            var errors = validation.ToDictionary()
                .ToDictionary(e => ToJsonPath(e.Key), e => e.Value, StringComparer.Ordinal);
            return AppError.Validation(errors);
        }

        var fingerprint = ComputeFingerprint(command);

        var existing = await orders.GetByIdempotencyKeyAsync(command.ClientName, command.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, fingerprint);
        }

        if (options.Value.ValidateAgainstCatalog)
        {
            var catalogError = await CheckAgainstCatalogAsync(command, cancellationToken).ConfigureAwait(false);
            if (catalogError is not null)
            {
                return catalogError;
            }
        }

        var order = SalesOrder.Create(
            command.ClientName,
            command.IdempotencyKey,
            fingerprint,
            command.CustomerCode,
            command.CustomerReference,
            command.Lines.Select(l => (l.Sku, l.Quantity)),
            clock.GetUtcNow());

        orders.Add(order);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException)
        {
            // A concurrent request with the same key was saved first; answer like a replay.
            unitOfWork.DiscardChanges();
            var winner = await orders.GetByIdempotencyKeyAsync(command.ClientName, command.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return Replay(winner, fingerprint);
        }

        dispatchSignal.Notify();
        LogOrderAccepted(logger, order.Id, order.CustomerCode, order.Lines.Count);

        return new CreateSalesOrderOutcome(order.Id, Created: true);
    }

    private static Result<CreateSalesOrderOutcome> Replay(SalesOrder existing, string fingerprint) =>
        string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
            ? new CreateSalesOrderOutcome(existing.Id, Created: false)
            : new AppError(
                "idempotency_key_reused",
                "This Idempotency-Key was already used for a different order. Use a new key for a new order.",
                ErrorKind.Conflict);

    private async Task<AppError?> CheckAgainstCatalogAsync(CreateSalesOrderCommand command, CancellationToken cancellationToken)
    {
        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var customers = await catalog.GetCustomersAsync([command.CustomerCode], cancellationToken).ConfigureAwait(false);
        if (!customers.TryGetValue(command.CustomerCode, out var customer) || !customer.IsActive)
        {
            problems["customerCode"] = [$"Customer '{command.CustomerCode}' is unknown or inactive."];
        }

        var skus = command.Lines.Select(l => l.Sku).Distinct(StringComparer.Ordinal).ToList();
        var products = await catalog.GetProductsAsync(skus, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < command.Lines.Count; i++)
        {
            var sku = command.Lines[i].Sku;
            if (!products.TryGetValue(sku, out var product) || !product.IsActive)
            {
                problems[$"lines[{i}].sku"] = [$"Product '{sku}' is unknown or inactive."];
            }
        }

        return problems.Count == 0
            ? null
            : new AppError("catalog_mismatch", "The order references unknown or inactive records.", ErrorKind.Unprocessable, problems);
    }

    private static string ToJsonPath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment =>
            segment.Length == 0 ? segment : string.Concat(char.ToLowerInvariant(segment[0]).ToString(), segment.AsSpan(1))));

    /// <summary>A stable hash of the business content of the request.</summary>
    internal static string ComputeFingerprint(CreateSalesOrderCommand command)
    {
        // JSON gives unambiguous field boundaries; "0.####" makes 1, 1.0 and 1.00 the same quantity.
        var canonical = JsonSerializer.Serialize(new
        {
            customer = command.CustomerCode.Trim(),
            reference = command.CustomerReference?.Trim(),
            lines = command.Lines.Select(l => new
            {
                sku = l.Sku.Trim(),
                qty = l.Quantity.ToString("0.############", CultureInfo.InvariantCulture),
            }),
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted sales order {OrderId} for customer {CustomerCode} with {LineCount} lines")]
    private static partial void LogOrderAccepted(ILogger logger, Guid orderId, string customerCode, int lineCount);
}
