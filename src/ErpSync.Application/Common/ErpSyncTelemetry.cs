using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ErpSync.Application.Common;

/// <summary>Tracing and metrics instruments. Compatible with any OpenTelemetry exporter.</summary>
public static class ErpSyncTelemetry
{
    public const string SourceName = "ErpSync";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Starts a new trace for background work. Falls back to a plain <see cref="Activity"/> when no
    /// listener is registered, so a trace id always exists for logs and outgoing calls.
    /// </summary>
    public static Activity StartRootActivity(string name)
    {
        // Background work starts its own trace rather than joining whatever happened to run before it.
        Activity.Current = null;
        return ActivitySource.StartActivity(name, ActivityKind.Internal) ?? new Activity(name).Start();
    }

    private static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> RecordsChanged =
        Meter.CreateCounter<long>("erpsync.sync.records_changed", description: "ERP records that changed the local copy.");

    public static readonly Counter<long> RecordsSkipped =
        Meter.CreateCounter<long>("erpsync.sync.records_skipped", description: "ERP records skipped because they were invalid.");

    public static readonly Counter<long> SyncRunsFailed =
        Meter.CreateCounter<long>("erpsync.sync.runs_failed", description: "Sync runs that ended in failure.");

    public static readonly Counter<long> OrdersDispatched =
        Meter.CreateCounter<long>("erpsync.orders.dispatched", description: "Sales order delivery attempts, by outcome.");
}
