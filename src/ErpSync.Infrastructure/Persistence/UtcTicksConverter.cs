using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ErpSync.Infrastructure.Persistence;

/// <summary>Stores a <see cref="DateTimeOffset"/> as UTC ticks. Values read back are in UTC.</summary>
internal sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
