namespace MeowFilm.Core.Runtime;

/// <summary>Process-wide candidate identities shared by native script consumers.</summary>
public static class RuntimeSourceRevision
{
    private static long current = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long Next()
    {
        var revision = Interlocked.Increment(ref current);
        if (revision is <= 0 or > BridgeCodec.MaximumSafeInteger) throw new BridgeException("INVALID_REQUEST");
        return revision;
    }
}
