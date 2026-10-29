using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Scripting;

/// <summary>Native selected-file snapshots. This is not an IPC or filesystem capability.</summary>
internal sealed class MusicNativeTransfers(TimeProvider clock) : IDisposable
{
    internal const int ScriptBytes = 512 * 1024, ChunkBytes = 16 * 1024;
    internal sealed record Snapshot(byte[] Bytes, string Basename, string Hash, string InstallationId) : IDisposable
    {
        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
        internal string Text()
        {
            if (Bytes.Length >= 3 && Bytes[0] == 0xef && Bytes[1] == 0xbb && Bytes[2] == 0xbf)
                throw new MusicLibraryException("SCRIPT_ENCODING");
            try { return new UTF8Encoding(false, true).GetString(Bytes); }
            catch (DecoderFallbackException) { throw new MusicLibraryException("SCRIPT_ENCODING"); }
        }
    }
    private sealed class Stage(string kind, int length, string hash, string basename, DateTimeOffset expires)
    {
        internal string Kind = kind, Hash = hash, Basename = basename;
        internal byte[] Bytes = new byte[length];
        internal int Offset;
        internal DateTimeOffset Expires = expires;
    }
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private bool closed;
    private void Sweep()
    {
        LJ.Require(!closed, "CLOSED");
        foreach (var key in stages.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) Release(key);
    }
    private void Release(string token)
    {
        if (stages.Remove(token, out var stage)) CryptographicOperations.ZeroMemory(stage.Bytes);
    }
    private Stage Get(string token)
    {
        Sweep();
        LJ.Require(stages.TryGetValue(token, out var value), "TRANSFER_NOT_FOUND");
        return value!;
    }
    internal JsonObject Begin(JsonObject input)
    {
        Sweep(); LJ.Fields(input, "kind length sha256 basename", "kind length sha256 basename");
        string kind = LJ.Text(input["kind"]), hash = LJ.Text(input["sha256"]), basename = LJ.Text(input["basename"], 255);
        LJ.Require(kind == "script");
        int length = (int)LJ.I(input["length"], 1, ScriptBytes);
        LJ.Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
        LJ.Require(!basename.Any(c => "\\/:*?\"<>|".Contains(c)) &&
            basename.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
        LJ.Require(stages.Count < 2 && stages.Values.Sum(s => (long)s.Bytes.Length) + length <= ScriptBytes * 2L, "LIMIT");
        string token = Guid.NewGuid().ToString("D");
        stages[token] = new(kind, length, hash, basename, clock.GetUtcNow().AddMinutes(5));
        return LJ.Obj(("token", token), ("installationId", "lx-" + token), ("chunkBytes", ChunkBytes));
    }
    internal JsonObject Chunk(JsonObject input)
    {
        LJ.Fields(input, "token offset data", "token offset data");
        var stage = Get(LJ.Text(input["token"], 80));
        long offset = LJ.I(input["offset"]); string data = LJ.Text(input["data"], 21848);
        LJ.Require(offset == stage.Offset, "TRANSFER_OFFSET");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw new MusicLibraryException("INVALID_REQUEST"); }
        try
        {
            LJ.Require(bytes.Length is > 0 and <= ChunkBytes && Convert.ToBase64String(bytes) == data &&
                offset + bytes.Length <= stage.Bytes.Length, "LIMIT");
            bytes.CopyTo(stage.Bytes, stage.Offset); stage.Offset += bytes.Length;
            return LJ.Obj(("next", stage.Offset));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal JsonObject Cancel(JsonObject input)
    {
        LJ.Fields(input, "token", "token"); Release(LJ.Text(input["token"], 80)); return new();
    }
    internal Snapshot Take(string token, string kind)
    {
        var stage = Get(token);
        LJ.Require(stage.Kind == kind && stage.Offset == stage.Bytes.Length, "TRANSFER_INCOMPLETE");
        stages.Remove(token);
        if (LJ.Hash(stage.Bytes) != stage.Hash)
        {
            CryptographicOperations.ZeroMemory(stage.Bytes);
            throw new MusicLibraryException("TRANSFER_INTEGRITY");
        }
        return new(stage.Bytes, stage.Basename, stage.Hash, "lx-" + token);
    }
    public void Dispose()
    {
        closed = true;
        foreach (string token in stages.Keys.ToArray()) Release(token);
    }
}
