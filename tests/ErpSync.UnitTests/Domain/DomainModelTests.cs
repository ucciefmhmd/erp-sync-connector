using ErpSync.Application.Orders;
using ErpSync.Domain.Catalog;
using ErpSync.Domain.Common;
using ErpSync.Domain.Orders;
using ErpSync.Domain.Sync;

namespace ErpSync.UnitTests.Domain;

public class DomainModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Product_ignores_updates_older_than_what_it_already_has()
    {
        var product = Product.Create("SKU-1", "Valve", 10m, "eur", true, Now, Now);

        var changed = product.ApplyUpdate("Old name", 5m, "EUR", true, Now.AddMinutes(-1), Now);

        Assert.False(changed);
        Assert.Equal("Valve", product.Name);
        Assert.Equal("EUR", product.Currency);
    }

    [Fact]
    public void Product_rejected_update_leaves_the_product_unchanged()
    {
        var product = Product.Create("SKU-1", "Valve", 10m, "EUR", true, Now, Now);

        Assert.Throws<DomainException>(() => product.ApplyUpdate("New name", -1m, "EUR", true, Now.AddMinutes(1), Now));

        Assert.Equal("Valve", product.Name);
        Assert.Equal(10m, product.UnitPrice);
        Assert.Equal(Now, product.ErpModifiedAt);
    }

    [Fact]
    public void Checkpoint_cannot_move_backwards()
    {
        var checkpoint = SyncCheckpoint.Start(SyncEntity.Products, Now);
        checkpoint.Advance(new SyncPosition(Now, "B"), Now);

        Assert.Throws<DomainException>(() => checkpoint.Advance(new SyncPosition(Now, "A"), Now));
        Assert.Equal("B", checkpoint.LastKey);
    }

    [Fact]
    public void Order_numbers_its_lines_and_starts_pending_and_due()
    {
        var order = NewOrder();

        Assert.Equal(SalesOrderStatus.Pending, order.Status);
        Assert.Equal(Now, order.NextAttemptAt);
        Assert.Equal([1, 2], order.Lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Order_claim_marks_outcome_unknown_until_the_attempt_finishes()
    {
        var order = NewOrder();

        order.BeginAttempt(Now.AddMinutes(2));

        Assert.Equal(1, order.Attempts);
        Assert.True(order.OutcomeUnknown);
        Assert.Equal(Now.AddMinutes(2), order.NextAttemptAt);
    }

    [Fact]
    public void Order_fails_permanently_when_attempts_run_out()
    {
        var order = NewOrder();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            order.BeginAttempt(Now.AddMinutes(2));
            order.RecordTransientFailure("HTTP 503", outcomeUnknown: false, Now.AddMinutes(attempt), maxAttempts: 3, Now);
        }

        Assert.Equal(SalesOrderStatus.Failed, order.Status);
        Assert.Null(order.NextAttemptAt);
        Assert.Contains("Gave up after 3 attempts", order.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Submitted_order_cannot_be_delivered_again()
    {
        var order = NewOrder();
        order.BeginAttempt(Now.AddMinutes(2));
        order.MarkSubmitted("SO-1", Now);

        Assert.Throws<DomainException>(() => order.BeginAttempt(Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData(1, 10_000)]
    [InlineData(2, 20_000)]
    [InlineData(3, 40_000)]
    [InlineData(20, 60_000)]
    public void Retry_backoff_doubles_per_attempt_and_is_capped(int attempt, double expectedMs)
    {
        // random = 0.5 gives a jitter factor of exactly 1.0
        var delay = RetryBackoff.Compute(attempt, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), random: 0.5);

        Assert.Equal(expectedMs, delay.TotalMilliseconds, precision: 3);
    }

    private static SalesOrder NewOrder() =>
        SalesOrder.Create("client-a", "key-1", "fingerprint", "C001", null, [("SKU-1", 1m), ("SKU-2", 2m)], Now);
}
