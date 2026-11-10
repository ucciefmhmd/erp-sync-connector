using ErpSync.Application.Abstractions.Erp;

namespace ErpSync.UnitTests.Support;

internal static class ErpData
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static ErpProduct Product(int number, int minute, string? name = null, decimal price = 10m, bool active = true) =>
        new($"SKU-{number:D3}", name ?? $"Product {number}", price, "EUR", active, T0.AddMinutes(minute));

    public static ErpCustomer Customer(string code, int minute, bool active = true) =>
        new(code, $"Customer {code}", $"{code.ToLowerInvariant()}@example.com", 100m, "EUR", active, T0.AddMinutes(minute));
}
