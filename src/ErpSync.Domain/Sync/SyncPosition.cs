namespace ErpSync.Domain.Sync;

/// <summary>
/// Keyset position in the ERP change feed. Records are read in (ModifiedAt, Key) order and each
/// page starts strictly after this position. Unlike offset paging, a record that changes while a
/// run is in progress moves to the end of the feed instead of shifting other records out of view.
/// </summary>
public sealed record SyncPosition(DateTimeOffset ModifiedAt, string Key) : IComparable<SyncPosition>
{
    public int CompareTo(SyncPosition? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byTime = ModifiedAt.CompareTo(other.ModifiedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(Key, other.Key);
    }

    public static bool operator <(SyncPosition? left, SyncPosition? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(SyncPosition? left, SyncPosition? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator <=(SyncPosition? left, SyncPosition? right) => !(left > right);

    public static bool operator >=(SyncPosition? left, SyncPosition? right) => !(left < right);
}
