using System.IO;

namespace MeowFilm.AppHost;

public sealed record AppDataPaths(string Documents)
{
    public static AppDataPaths Current => ForIdentity(AppIdentity.StorageId);
    /// <summary>Fixed App-identity LocalAppData location, never a caller-supplied user path.</summary>
    public static AppDataPaths ForIdentity(string identity)
    {
        if (identity is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterOrDigit(identity[0]) ||
            identity.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("INVALID_PRODUCT_ID", nameof(identity));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local)) throw new InvalidOperationException("PRODUCT_DATA_UNAVAILABLE");
        string documents = Path.Combine(local, "meowfilm", identity);
        return new(documents);
    }
}
