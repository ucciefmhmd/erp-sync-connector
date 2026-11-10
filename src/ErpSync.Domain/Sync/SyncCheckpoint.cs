using ErpSync.Domain.Common;

namespace ErpSync.Domain.Sync;

/// <summary>How far the connector has read the ERP change feed for one entity type.</summary>
public sealed class SyncCheckpoint
{
    private SyncCheckpoint()
    {
    }

    public SyncEntity Entity { get; private set; }

    public DateTimeOffset? LastModifiedAt { get; private set; }

    public string? LastKey { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public SyncPosition? Position =>
        LastModifiedAt is { } modifiedAt && LastKey is not null ? new SyncPosition(modifiedAt, LastKey) : null;

    public static SyncCheckpoint Start(SyncEntity entity, DateTimeOffset now) =>
        new() { Entity = entity, UpdatedAt = now };

    /// <summary>Moves the checkpoint forward. Moving backwards would indicate a bug, so it is refused.</summary>
    public void Advance(SyncPosition position, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(position);

        if (position < Position)
        {
            throw new DomainException($"Checkpoint for {Entity} cannot move backwards.");
        }

        LastModifiedAt = position.ModifiedAt;
        LastKey = position.Key;
        UpdatedAt = now;
    }
}
