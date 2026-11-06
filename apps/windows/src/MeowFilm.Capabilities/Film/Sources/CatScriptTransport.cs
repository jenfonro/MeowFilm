using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

// Cat SourceTransfers carries raw JSON in data/blob; music's blob carries an envelope.
internal sealed class CatScriptTransport(IRuntimeClient runtime)
{
    private const int ChunkBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<JsonElement> CallAsync(string operation, object parameters, CancellationToken token)
    {
        var reply = await RpcAsync(operation, parameters, token).ConfigureAwait(false);
        if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("blob", out var blob))
            return await ReadBlobAsync(reply, blob, token).ConfigureAwait(false);
        Success(reply);
        token.ThrowIfCancellationRequested();
        return Parse(Text(reply, "data"));
    }

    internal Task<JsonElement> WithSourceAsync(string source,
        Func<string, CancellationToken, Task<JsonElement>> consume, CancellationToken token) =>
        WithUploadAsync(Utf8.GetBytes(source), "source", consume, token);

    internal async Task<JsonElement> InvokeAsync(FilmCatSource cat, FilmVideoSource site,
        string operation, object payload, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > ChunkBytes)
            return await WithUploadAsync(bytes, "json", (upload, ct) => CallAsync("invoke", new
            {
                installationId = cat.InstallationId, generation = cat.Generation,
                siteId = site.Key.SiteKey, operation, payloadToken = upload
            }, ct), token).ConfigureAwait(false);
        try
        {
            return await CallAsync("invoke", new
            {
                installationId = cat.InstallationId, generation = cat.Generation,
                siteId = site.Key.SiteKey, operation, payload = Utf8.GetString(bytes)
            }, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<JsonElement> WithUploadAsync(byte[] bytes, string kind,
        Func<string, CancellationToken, Task<JsonElement>> consume, CancellationToken token)
    {
        string? upload = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var begin = await RpcAsync("stage.begin", new
            {
                length = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), kind
            }, token).ConfigureAwait(false);
            upload = OptionalText(begin, "token");
            Success(begin);
            Require(!string.IsNullOrEmpty(upload) && Integer(begin, "chunkBytes") == ChunkBytes);
            for (int offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(ChunkBytes, bytes.Length - offset);
                var part = await RpcAsync("stage.chunk", new
                {
                    token = upload, offset, data = Convert.ToBase64String(bytes, offset, count)
                }, token).ConfigureAwait(false);
                Success(part);
                Require(Integer(part, "next") == (long)offset + count);
                offset += count;
            }
            token.ThrowIfCancellationRequested();
            return await consume(upload!, token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (upload is not null) await CleanupAsync("stage.abort", upload).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> ReadBlobAsync(JsonElement envelope, JsonElement blob, CancellationToken token)
    {
        string? receipt = OptionalText(blob, "token");
        using var buffer = new MemoryStream();
        try
        {
            Success(envelope);
            Require(!string.IsNullOrEmpty(receipt) && Integer(blob, "chunkBytes") == ChunkBytes);
            long length = Integer(blob, "length");
            // MemoryStream/JsonDocument use a single CLR buffer. This is a resource boundary.
            Require(length is > 0 and <= int.MaxValue);
            string hash = Text(blob, "sha256");
            Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < length;)
            {
                token.ThrowIfCancellationRequested();
                var reply = await RpcAsync("blob.read", new { token = receipt, offset }, token).ConfigureAwait(false);
                Success(reply);
                string encoded = Text(reply, "data");
                Require(encoded.Length <= ((ChunkBytes + 2) / 3) * 4);
                byte[] chunk;
                try { chunk = Convert.FromBase64String(encoded); }
                catch (FormatException) { throw new FilmSourceException("INVALID_RESPONSE"); }
                try
                {
                    Require(Convert.ToBase64String(chunk) == encoded &&
                        chunk.Length == Math.Min(ChunkBytes, length - offset));
                    long next = offset + chunk.Length;
                    Require(Integer(reply, "next") == next);
                    digest.AppendData(chunk);
                    buffer.Write(chunk);
                    offset = next;
                }
                finally { CryptographicOperations.ZeroMemory(chunk); }
            }
            token.ThrowIfCancellationRequested();
            Require(buffer.Length == length && Convert.ToHexStringLower(digest.GetHashAndReset()) == hash);
            try
            {
                using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
                return document.RootElement.Clone();
            }
            catch (JsonException) { throw new FilmSourceException("INVALID_RESPONSE"); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (receipt is not null) await CleanupAsync("blob.release", receipt).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> RpcAsync(string operation, object parameters, CancellationToken token)
    {
        try { return await runtime.CallAsync("cat.script." + operation, parameters, token).ConfigureAwait(false); }
        catch (BridgeException error) when (error.Code == "CANCELLED")
        { throw new OperationCanceledException(error.Message, error, token); }
    }

    private async Task CleanupAsync(string operation, string receipt)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await RpcAsync(operation, new { token = receipt }, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch { /* SourceTransfers owns expiry when the connection is unavailable. */ }
    }

    internal static JsonElement Parse(string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { throw new FilmSourceException("INVALID_RESPONSE"); }
    }

    private static void Success(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("ok", out var ok) &&
            ok.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            OptionalText(value, "contract") == "cat.script.v1");
        if (!value.GetProperty("ok").GetBoolean())
        {
            string code = Text(value, "code");
            if (code == "CANCELLED") throw new OperationCanceledException(code);
            throw new FilmSourceException(code);
        }
    }

    internal static string Text(JsonElement value, string property)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
            text.ValueKind == JsonValueKind.String);
        return value.GetProperty(property).GetString()!;
    }

    internal static string? OptionalText(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    internal static long Integer(JsonElement value, string property)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var number) &&
            number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out long integer) &&
            integer >= 0 && integer <= BridgeCodec.MaximumSafeInteger);
        return value.GetProperty(property).GetInt64();
    }

    internal static void Require(bool condition)
    {
        if (!condition) throw new FilmSourceException("INVALID_RESPONSE");
    }
}
