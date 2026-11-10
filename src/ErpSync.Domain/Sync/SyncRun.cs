namespace ErpSync.Domain.Sync;

public enum SyncRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>Audit record of one sync run, exposed through the API for troubleshooting.</summary>
public sealed class SyncRun
{
    private SyncRun()
    {
    }

    public Guid Id { get; private set; }

    public SyncEntity Entity { get; private set; }

    public SyncRunStatus Status { get; private set; }

    /// <summary>What started the run, for example "schedule" or "manual".</summary>
    public string Trigger { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int Pages { get; private set; }

    public int RecordsFetched { get; private set; }

    public int RecordsChanged { get; private set; }

    /// <summary>Records the ERP sent that failed validation and were skipped (see logs for details).</summary>
    public int RecordsSkipped { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Distributed trace id, so a run can be matched with its logs.</summary>
    public string? TraceId { get; private set; }

    public static SyncRun Start(SyncEntity entity, string trigger, string? traceId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Entity = entity,
        Trigger = trigger,
        TraceId = traceId,
        Status = SyncRunStatus.Running,
        StartedAt = now,
    };

    public void RecordPage(int fetched, int changed, int skipped)
    {
        Pages++;
        RecordsFetched += fetched;
        RecordsChanged += changed;
        RecordsSkipped += skipped;
    }

    public void Succeed(DateTimeOffset now)
    {
        Status = SyncRunStatus.Succeeded;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        Status = SyncRunStatus.Failed;
        Error = error.Length <= 1000 ? error : error[..1000];
        CompletedAt = now;
    }
}
