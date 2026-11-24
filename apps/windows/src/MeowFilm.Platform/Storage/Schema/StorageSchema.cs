using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.Platform.Storage;

internal readonly record struct EmbeddedStorageSchema(string Sql, string Hash);

internal static class StorageSchema
{
    public const string DataResourceName = "MeowFilm.Storage.data-schema.sql";
    public const string CacheResourceName = "MeowFilm.Storage.cache-schema.sql";

    public static string ResourceNameFor(DatabaseRole role) => role switch
    {
        DatabaseRole.Data => DataResourceName,
        DatabaseRole.Cache => CacheResourceName,
        _ => throw new StorageException(StorageError.InvalidRequest)
    };

    public static EmbeddedStorageSchema Read(DatabaseRole role = DatabaseRole.Data)
    {
        var assembly = typeof(NativeDatabase).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceNameFor(role))
            ?? throw new StorageException(StorageError.UnsupportedVersion);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return new EmbeddedStorageSchema(
            new UTF8Encoding(false, true).GetString(bytes),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static void Verify(DatabaseSession actual, EmbeddedStorageSchema schema)
    {
        if (actual.Scalar<string>("SELECT schema_hash FROM storage_identity WHERE singleton=1") != schema.Hash)
            throw new StorageException(StorageError.UnsupportedVersion);
    }
}
