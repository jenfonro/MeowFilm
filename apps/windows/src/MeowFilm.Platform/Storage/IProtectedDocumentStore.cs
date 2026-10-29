using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Storage;

/// <summary>Content CAS, not a monotonic revision: A-to-B-to-A is indistinguishable.</summary>
public sealed record DocumentFingerprint(bool Exists, int Length, string? Sha256)
{
    public static DocumentFingerprint Missing { get; } = new(false, 0, null);
    public static DocumentFingerprint FromBytes(ReadOnlySpan<byte> bytes) =>
        new(true, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    internal bool IsValid => Length >= 0 && Length <= DocumentRules.MaximumDocumentBytes &&
        (Exists ? DocumentRules.ValidHash(Sha256) : Length == 0 && Sha256 is null);
}

/// <summary>Caller owns Data. Null means absent; an existing empty byte array does not.</summary>
public sealed record ProtectedDocument(byte[]? Data, DocumentFingerprint Fingerprint);

public enum DocumentStoreError
{
    InvalidRequest, Unavailable, UnsafePath, Limit, Conflict, Corrupt,
    UnsupportedVersion, Protection, Storage, Closed
}

/// <summary>Fixed diagnostics only: never contains paths, payloads or an inner exception.</summary>
public sealed class ProtectedDocumentException : Exception
{
    public DocumentStoreError Error { get; }
    public ProtectedDocumentException(DocumentStoreError error) : base(error.ToString()) => Error = error;
}

public interface IProtectedDocumentStore : IAsyncDisposable
{
    Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default);

    /// <summary>Atomically compare plaintext existence/length/hash and replace. No blind write.</summary>
    Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default);
}

internal static class DocumentRules
{
    internal const int MaximumDocumentBytes = 8 * 1024 * 1024;
    internal static bool ValidNamespace(string? value)
    {
        if (value is null || value.Length is < 1 or > 128 || !AlphaNumeric(value[0]) ||
            value.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in value) if (!AlphaNumeric(c) && c is not ('.' or '_' or '-')) return false;
        return true;
    }

    private static bool AlphaNumeric(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    internal static bool ValidHash(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char c in value) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    internal static HashSet<string> Namespaces(IEnumerable<string> values)
    {
        if (values is null) throw new ProtectedDocumentException(DocumentStoreError.InvalidRequest);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
            if (!ValidNamespace(value) || !result.Add(value) || result.Count > 64)
                throw new ProtectedDocumentException(DocumentStoreError.InvalidRequest);
        return result;
    }
}
