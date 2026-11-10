using System.Globalization;

namespace ErpSync.MockErp;

public sealed record MockProduct(string Sku, string Name, decimal UnitPrice, string Currency, bool IsActive, DateTimeOffset ModifiedAt);

public sealed record MockStockLevel(string Sku, string Warehouse, decimal QuantityOnHand, DateTimeOffset ModifiedAt)
{
    public string Key => $"{Sku}|{Warehouse}";
}

public sealed record MockCustomer(string Code, string Name, string? Email, decimal Balance, string Currency, bool IsActive, DateTimeOffset ModifiedAt);

public sealed record MockOrderLine(string Sku, decimal Quantity);

public sealed record MockOrderRequest(string ExternalReference, string CustomerCode, string? CustomerReference, IReadOnlyList<MockOrderLine> Lines);

public sealed record MockOrder(string OrderNumber, string ExternalReference, string CustomerCode, IReadOnlyList<MockOrderLine> Lines, DateTimeOffset CreatedAt);

/// <summary>In-memory ERP data. Every change gets a fresh, strictly increasing ModifiedAt, like a real change feed.</summary>
public sealed class ErpStore
{
    private static readonly string[] Warehouses = ["CAI-01", "RUH-01"];
    private static readonly string[] Nouns = ["Bracket", "Cable", "Sensor", "Valve", "Panel", "Filter", "Switch", "Pump", "Relay", "Gasket"];
    private static readonly string[] Adjectives = ["Steel", "Compact", "Industrial", "Heavy-duty", "Smart", "Mini", "Pro", "Basic"];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, MockProduct> _products = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MockStockLevel> _stock = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MockCustomer> _customers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MockOrder> _ordersByReference = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastStamp;
    private int _orderSequence;

    public ErpStore(TimeProvider clock)
    {
        _clock = clock;
        Seed(new Random(42));
    }

    public (IReadOnlyList<T> Items, bool HasMore) ReadFeed<T>(
        Func<ErpStore, IEnumerable<T>> source, Func<T, DateTimeOffset> modifiedAt, Func<T, string> key,
        DateTimeOffset? afterModifiedAt, string? afterKey, int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_lock)
        {
            var ordered = source(this)
                .Where(item => afterModifiedAt is null
                    || modifiedAt(item) > afterModifiedAt
                    || (modifiedAt(item) == afterModifiedAt && string.CompareOrdinal(key(item), afterKey) > 0))
                .OrderBy(modifiedAt)
                .ThenBy(key, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToList();

            return (ordered.Take(limit).ToList(), ordered.Count > limit);
        }
    }

    public IEnumerable<MockProduct> Products => _products.Values;

    public IEnumerable<MockStockLevel> StockLevels => _stock.Values;

    public IEnumerable<MockCustomer> Customers => _customers.Values;

    public MockOrder? FindOrder(string externalReference)
    {
        lock (_lock)
        {
            return _ordersByReference.GetValueOrDefault(externalReference);
        }
    }

    /// <summary>Creates the order, or returns the existing one for the same idempotency key.</summary>
    public (MockOrder? Order, bool Created, string? Error) CreateOrder(string idempotencyKey, MockOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            if (_ordersByReference.TryGetValue(idempotencyKey, out var existing))
            {
                return (existing, false, null);
            }

            if (!_customers.TryGetValue(request.CustomerCode, out var customer) || !customer.IsActive)
            {
                return (null, false, $"Customer '{request.CustomerCode}' does not exist or is blocked.");
            }

            if (request.Lines is null || request.Lines.Count == 0)
            {
                return (null, false, "An order needs at least one line.");
            }

            foreach (var line in request.Lines)
            {
                if (!_products.TryGetValue(line.Sku, out var product) || !product.IsActive)
                {
                    return (null, false, $"Item '{line.Sku}' does not exist or is blocked.");
                }

                if (line.Quantity <= 0)
                {
                    return (null, false, $"Quantity for '{line.Sku}' must be positive.");
                }
            }

            var order = new MockOrder(
                $"SO-{++_orderSequence:D6}", idempotencyKey, request.CustomerCode, request.Lines, _clock.GetUtcNow());
            _ordersByReference[idempotencyKey] = order;
            return (order, true, null);
        }
    }

    /// <summary>Simulates day-to-day activity: stock moves, price changes, a new product now and then.</summary>
    public void Mutate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        lock (_lock)
        {
            var stockKeys = _stock.Keys.ToList();
            for (var i = 0; i < 5; i++)
            {
                var level = _stock[stockKeys[random.Next(stockKeys.Count)]];
                var quantity = Math.Max(0, level.QuantityOnHand + random.Next(-20, 25));
                _stock[level.Key] = level with { QuantityOnHand = quantity, ModifiedAt = NextStamp() };
            }

            var skus = _products.Keys.Where(s => !s.StartsWith("BROKEN", StringComparison.Ordinal)).ToList();
            var product = _products[skus[random.Next(skus.Count)]];
            var newPrice = Math.Round(product.UnitPrice * (decimal)(0.95 + (random.NextDouble() * 0.1)), 2);
            _products[product.Sku] = product with { UnitPrice = newPrice, ModifiedAt = NextStamp() };

            if (random.NextDouble() < 0.2)
            {
                AddProduct(random, _products.Count + 1);
            }
        }
    }

    private void Seed(Random random)
    {
        _lastStamp = _clock.GetUtcNow().AddDays(-30);

        for (var i = 1; i <= 120; i++)
        {
            AddProduct(random, i);
        }

        // A record that breaks the connector's validation rules (empty name). The connector should
        // log and skip it, not get stuck on it.
        _products["BROKEN-0001"] = new MockProduct("BROKEN-0001", string.Empty, 10m, "EUR", true, NextStamp());

        for (var i = 1; i <= 40; i++)
        {
            var code = $"C{i:D4}";
            _customers[code] = new MockCustomer(
                code,
                $"Customer {i:D2} Trading",
                $"accounts{i}@example.com",
                Math.Round((decimal)(random.NextDouble() * 5000), 2),
                "EUR",
                IsActive: i % 13 != 0,
                NextStamp());
        }
    }

    private void AddProduct(Random random, int number)
    {
        var sku = $"SKU-{number:D5}";
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{Adjectives[random.Next(Adjectives.Length)]} {Nouns[random.Next(Nouns.Length)]} {number}");
        _products[sku] = new MockProduct(
            sku, name, Math.Round((decimal)(5 + (random.NextDouble() * 495)), 2), "EUR", IsActive: number % 17 != 0, NextStamp());

        foreach (var warehouse in Warehouses)
        {
            var level = new MockStockLevel(sku, warehouse, random.Next(0, 500), NextStamp());
            _stock[level.Key] = level;
        }
    }

    /// <summary>A strictly increasing timestamp, never earlier than the clock.</summary>
    private DateTimeOffset NextStamp()
    {
        var now = _clock.GetUtcNow();
        _lastStamp = now > _lastStamp ? now : _lastStamp.AddTicks(1);
        return _lastStamp;
    }
}
