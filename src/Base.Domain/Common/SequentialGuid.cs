using System.Buffers.Binary;

namespace Base.Domain.Common;

/// <summary>
/// GUIDs that are sequential in SQL Server's <c>uniqueidentifier</c> sort order, so inserts append to the clustered
/// index instead of splitting pages. Same layout as EF Core's SQL Server value generator, usable before an entity is
/// added (e.g. to build a storage key from the id).
/// </summary>
/// <remarks>
/// Guid v7 is <em>not</em> a substitute on SQL Server: it puts its timestamp in the first bytes, but SQL Server
/// compares the last six bytes first, so v7 values look random there.
/// </remarks>
public static class SequentialGuid
{
    private static long _counter = TimeProvider.System.GetUtcNow().UtcTicks;

    public static Guid Next()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes);

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, Interlocked.Increment(ref _counter));

        // SQL Server orders by bytes 10-15 first, then 8-9: put the counter there, most significant first.
        bytes[10] = counter[2];
        bytes[11] = counter[3];
        bytes[12] = counter[4];
        bytes[13] = counter[5];
        bytes[14] = counter[6];
        bytes[15] = counter[7];
        bytes[8] = counter[0];
        bytes[9] = counter[1];

        return new Guid(bytes);
    }
}
