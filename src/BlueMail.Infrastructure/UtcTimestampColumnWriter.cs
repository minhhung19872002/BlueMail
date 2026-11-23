using NpgsqlTypes;
using Serilog.Events;
using Serilog.Sinks.PostgreSQL;

namespace BlueMail.Infrastructure;

/// <summary>
/// Log timestamp for PostgreSQL in UTC. Npgsql only writes DateTimeOffset with offset 0 to timestamptz, so the
/// stock TimestampColumnWriter lost every batch on hosts whose local time is not UTC (BUG-021).
/// </summary>
public sealed class UtcTimestampColumnWriter() : ColumnWriterBase(NpgsqlDbType.TimestampTz)
{
    public override object GetValue(LogEvent logEvent, IFormatProvider? formatProvider = null) => logEvent.Timestamp.ToUniversalTime();
}
