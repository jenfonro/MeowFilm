using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities.Scripting;

internal sealed class MusicScriptTransport(IRuntimeClient runtime)
{
    private const int ChunkBytes = 64 * 1024;
    internal const string Contract = "music.script.v3";

    /// <summary>Business operations transparently upload large parameters and read back large results;
    /// stage/control/blob are the transport itself.</summary>
    internal async Task<JsonElement> CallAsync(string operation, object parameters, CancellationToken cancellationToken)
    {
        if (operation.StartsWith("stage.", StringComparison.Ordinal) || operation.StartsWith("control.", StringComparison.Ordinal) ||
            operation.StartsWith("blob.", StringComparison.Ordinal))
            return await Rpc(operation, parameters, cancellationToken).ConfigureAwait(false);

        byte[] bytes = BridgeCodec.EncodeJSONValue(parameters);
        string? upload = null;
        JsonElement reply;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length <= ChunkBytes)
                reply = await Rpc(operation, parameters, cancellationToken).ConfigureAwait(false);
            else
            {
                var begin = await Rpc("control.begin", new { length = bytes.Length,
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, cancellationToken).ConfigureAwait(false);
                upload = Token(begin);
                Success(begin);
                Require(upload is not null && Integer(begin, "chunkBytes") == ChunkBytes);
                for (int offset = 0; offset < bytes.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(ChunkBytes, bytes.Length - offset);
                    var chunk = await Rpc("control.chunk", new { token = upload, offset,
                        data = Convert.ToBase64String(bytes, offset, count) }, cancellationToken).ConfigureAwait(false);
                    Success(chunk);
                    Require(Integer(chunk, "next") == (long)offset + count);
                    offset += count;
                }
                cancellationToken.ThrowIfCancellationRequested();
                reply = await Rpc("control.call", new { operation, token = upload }, cancellationToken).ConfigureAwait(false);
            }
            // Capture a received blob before checking cancellation so its token
            // is still released when the business result races cancellation.
            if (reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("blob", out var blob))
                reply = await ReadBlobAsync(reply, blob, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (upload is not null) await CleanupAsync("control.abort", upload).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return reply;
    }

    private async Task<JsonElement> ReadBlobAsync(JsonElement envelope, JsonElement blob, CancellationToken cancellationToken)
    {
        string? token = Token(blob);
        using var buffer = new MemoryStream();
        try
        {
            Success(envelope);
            Require(envelope.EnumerateObject().Count() == 3 && token is not null);
            long length = Integer(blob, "length");
            Require(length > 0 && Integer(blob, "chunkBytes") == ChunkBytes);
            Require(blob.TryGetProperty("sha256", out var hashValue) && hashValue.ValueKind == JsonValueKind.String);
            string hash = hashValue.GetString()!;
            Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reply = await Rpc("blob.read", new { token, offset }, cancellationToken).ConfigureAwait(false);
                Success(reply);
                Require(reply.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String);
                string encoded = data.GetString()!;
                Require(encoded.Length <= ((ChunkBytes + 2) / 3) * 4);
                byte[] chunk;
                try { chunk = Convert.FromBase64String(encoded); }
                catch (FormatException) { throw new MusicLibraryException("INVALID_RESPONSE"); }
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
            cancellationToken.ThrowIfCancellationRequested();
            Require(buffer.Length == length && Convert.ToHexStringLower(digest.GetHashAndReset()) == hash);
            JsonElement result;
            try { result = BridgeCodec.DecodeJSONValue(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)); }
            catch (BridgeException) { throw new MusicLibraryException("INVALID_RESPONSE"); }
            Success(result);
            Require(!result.TryGetProperty("blob", out _));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (token is not null) await CleanupAsync("blob.release", token).ConfigureAwait(false);
        }
    }

    private Task<JsonElement> Rpc(string operation, object parameters, CancellationToken token) =>
        runtime.CallAsync("music.script." + operation, parameters, token);

    private async Task CleanupAsync(string operation, string token)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Rpc(operation, new { token }, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch { /* Unreachable receipts and failed cleanup expire in the Node owner. */ }
    }

    private static string? Token(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("token", out var token) &&
        token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } text ? text : null;

    private static long Integer(JsonElement value, string property)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out _));
        var number = value.GetProperty(property);
        Require(number.ValueKind == JsonValueKind.Number);
        double integer = number.GetDouble();
        Require(double.IsFinite(integer) && Math.Truncate(integer) == integer &&
            integer >= 0 && integer <= BridgeCodec.MaximumSafeInteger);
        return (long)integer;
    }

    private static void Success(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("ok", out var ok) &&
            ok.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.TryGetProperty("contract", out var contract) && contract.ValueKind == JsonValueKind.String &&
            contract.GetString() == Contract);
        if (!value.GetProperty("ok").GetBoolean())
        {
            Require(value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String);
            throw new MusicLibraryException(value.GetProperty("code").GetString()!);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new MusicLibraryException("INVALID_RESPONSE");
    }
}
