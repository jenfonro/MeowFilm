using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Platform.Runtime;

/// <summary>A settings-only capability on the PID-verified private lifetime pipe. No arbitrary namespaces or files.</summary>
internal sealed class RuntimeSettingsPipe(Stream stream, IDocumentStore? store,
    IReadOnlyDictionary<string, string>? namespaces)
{
    private const int MaximumFrameBytes = 8 * 1024 * 1024;
    private readonly byte[] buffer = new byte[4096];
    private int offset, count;

    internal async Task<string> ReadyAsync(CancellationToken token)
    {
        for (;;)
        {
            var line = await ReadAsync(token).ConfigureAwait(false) ?? throw new BridgeException("CLOSED");
            try
            {
                var value = BridgeCodec.DecodeJSONValue(line);
                if (value.TryGetProperty("protocol", out _))
                {
                    if (BridgeCodec.RequiredProperty(value, "protocol").GetString() != RuntimeHttpProtocol.Name)
                        throw new BridgeException("INVALID_RESPONSE");
                    string endpoint = BridgeCodec.RequiredProperty(value, "baseUrl").GetString()!;
                    _ = RuntimeHttpProtocol.ValidateEndpoint(endpoint);
                    return endpoint;
                }
                await RespondAsync(value, token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(line); }
        }
    }

    internal async Task<string> RunAsync()
    {
        try
        {
            for (;;)
            {
                var line = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null) return "CLOSED";
                try
                {
                    await RespondAsync(BridgeCodec.DecodeJSONValue(line), CancellationToken.None).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(line); }
            }
        }
        catch (BridgeException) { return "INVALID_RESPONSE"; }
        catch (JsonException) { return "INVALID_RESPONSE"; }
        catch (ObjectDisposedException) { return "CLOSED"; }
        catch (InvalidOperationException) { return "INVALID_RESPONSE"; }
        catch (FormatException) { return "INVALID_RESPONSE"; }
        catch (IOException) { return "CLOSED"; }
    }

    private async Task RespondAsync(JsonElement request, CancellationToken token)
    {
        if (store is null || namespaces is null ||
            !BridgeCodec.RequiredProperty(request, "id").TryGetInt64(out long id) ||
            id is < 1 or > BridgeCodec.MaximumSafeInteger ||
            BridgeCodec.RequiredProperty(request, "block").GetString() is not { } block ||
            BridgeCodec.RequiredProperty(request, "kind").GetString() is not { } kind ||
            !namespaces.TryGetValue(block, out var documentNamespace))
            throw new BridgeException("INVALID_RESPONSE");
        if (kind is not ("settings.read" or "settings.write") ||
            kind == "settings.write" && block != "pan.accounts")
            throw new BridgeException("INVALID_RESPONSE");
        object reply;
        byte[]? data = null;
        try
        {
            DocumentFingerprint fingerprint;
            if (kind == "settings.read")
            {
                var snapshot = await store.ReadAsync(documentNamespace, token).ConfigureAwait(false);
                data = snapshot.Data;
                fingerprint = snapshot.Fingerprint;
            }
            else
            {
                var expected = BridgeCodec.RequiredProperty(request, "expected");
                var hash = BridgeCodec.RequiredProperty(expected, "sha256");
                var before = new DocumentFingerprint(BridgeCodec.RequiredProperty(expected, "exists").GetBoolean(),
                    BridgeCodec.RequiredProperty(expected, "length").GetInt32(),
                    hash.ValueKind == JsonValueKind.Null ? null : hash.GetString());
                data = BridgeCodec.EncodeJSONValue(BridgeCodec.RequiredProperty(request, "document"));
                fingerprint = await store.WriteAsync(documentNamespace, data, before, token).ConfigureAwait(false);
            }
            using var json = data is null ? null : JsonDocument.Parse(data);
            reply = new { kind = "settings.result", id, ok = true, document = json?.RootElement.Clone(),
                fingerprint = new { exists = fingerprint.Exists, length = fingerprint.Length, sha256 = fingerprint.Sha256 } };
        }
        catch (DocumentStoreException error)
        {
            reply = new { kind = "settings.result", id, ok = false,
                code = error.Error == DocumentStoreError.Conflict ? "STORAGE_CONFLICT" : "STORAGE_ERROR" };
        }
        finally { if (data is not null) CryptographicOperations.ZeroMemory(data); }
        byte[] response = BridgeCodec.EncodeJSONValue(reply);
        try
        {
            if (response.Length > MaximumFrameBytes) throw new BridgeException("INVALID_RESPONSE");
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(response); }
    }

    private async Task<byte[]?> ReadAsync(CancellationToken token)
    {
        using var line = new MemoryStream();
        try
        {
            for (;;)
            {
                if (offset == count)
                {
                    count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                    offset = 0;
                    if (count == 0)
                    {
                        if (line.Length != 0) throw new BridgeException("INVALID_RESPONSE");
                        return null;
                    }
                }
                int newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                int length = (newline < 0 ? count : newline) - offset;
                if (line.Length + length > MaximumFrameBytes) throw new BridgeException("INVALID_RESPONSE");
                line.Write(buffer, offset, length);
                CryptographicOperations.ZeroMemory(buffer.AsSpan(offset, length));
                offset += length;
                if (newline < 0) continue;
                offset++;
                return line.ToArray();
            }
        }
        finally { if (line.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes); }
    }
}
