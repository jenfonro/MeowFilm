using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Platform.Runtime;

/// <summary>A settings-only capability on the PID-verified private lifetime pipe. No arbitrary namespaces or files.</summary>
internal sealed class RuntimeSettingsPipe(Stream stream, IRuntimeSettingsStore? store)
{
    private readonly byte[] buffer = new byte[4096];
    private int offset, count;

    internal async Task<Uri> ReadyAsync(CancellationToken token)
    {
        try
        {
            for (;;)
            {
                var line = await ReadAsync(token, store is null ? 64 * 1024 : null).ConfigureAwait(false)
                    ?? throw new BridgeException("CLOSED");
                try
                {
                    var value = BridgeCodec.DecodeJSONValue(line);
                    if (value.TryGetProperty("protocol", out _))
                    {
                        if (line.Length > 64 * 1024 || offset != count ||
                            BridgeCodec.RequiredProperty(value, "protocol").GetString() != RuntimeHttpProtocol.Name)
                            throw new BridgeException("INVALID_RESPONSE");
                        string endpoint = BridgeCodec.RequiredProperty(value, "baseUrl").GetString()!;
                        return RuntimeHttpProtocol.ValidateEndpoint(endpoint);
                    }
                    await RespondAsync(value, token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(line); }
            }
        }
        catch (BridgeException error) when (error.Code != "CLOSED") { throw new BridgeException("INVALID_RESPONSE"); }
        catch (JsonException) { throw new BridgeException("INVALID_RESPONSE"); }
        catch (InvalidOperationException) { throw new BridgeException("INVALID_RESPONSE"); }
    }

    internal async Task<string> RunAsync()
    {
        try
        {
            if (store is null)
                return offset == count && await stream.ReadAsync(buffer.AsMemory(0, 1), CancellationToken.None).ConfigureAwait(false) == 0
                    ? "CLOSED" : "INVALID_RESPONSE";
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
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private async Task RespondAsync(JsonElement request, CancellationToken token)
    {
        if (store is null ||
            !BridgeCodec.RequiredProperty(request, "id").TryGetInt64(out long id) ||
            id is < 1 or > BridgeCodec.MaximumSafeInteger ||
            BridgeCodec.RequiredProperty(request, "block").GetString() is not { } block ||
            BridgeCodec.RequiredProperty(request, "kind").GetString() is not { } kind ||
            block is not ("cat.sources" or "pan.accounts" or "tmdb" or "douban" or "film.rules" or "film.preferences"))
            throw new BridgeException("INVALID_RESPONSE");
        if (kind is not ("settings.read" or "settings.pan.update") ||
            kind == "settings.pan.update" && block != "pan.accounts")
            throw new BridgeException("INVALID_RESPONSE");
        object reply;
        try
        {
            var snapshot = kind == "settings.read" ? await store.ReadAsync(block, token).ConfigureAwait(false) :
                await store.UpdatePanAsync(BridgeCodec.RequiredProperty(request, "command"), token).ConfigureAwait(false);
            reply = new { kind = "settings.result", id, ok = true, document = snapshot.Document, revision = snapshot.Revision };
        }
        catch (DocumentStoreException error)
        {
            reply = new { kind = "settings.result", id, ok = false, code = error.Error switch
            {
                DocumentStoreError.Conflict => "STORAGE_CONFLICT",
                DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion => "CORRUPT_STORAGE",
                DocumentStoreError.InvalidRequest => "INVALID_REQUEST",
                DocumentStoreError.Closed => "CLOSED",
                _ => "STORAGE_ERROR"
            } };
        }
        catch (BridgeException error)
        {
            reply = new { kind = "settings.result", id, ok = false, code = error.Code is "INVALID_REQUEST" ? "INVALID_REQUEST" : "STORAGE_ERROR" };
        }
        byte[] response = BridgeCodec.EncodeJSONValue(reply);
        try
        {
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(response); }
    }

    private async Task<byte[]?> ReadAsync(CancellationToken token, int? maximum = null)
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
                if (maximum is { } limit && line.Length + length > limit) throw new BridgeException("INVALID_RESPONSE");
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
