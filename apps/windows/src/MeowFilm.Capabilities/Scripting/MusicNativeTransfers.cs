using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities.Scripting;

/// <summary>Native selected-file snapshots. This is not an IPC or filesystem capability.</summary>
internal sealed class MusicNativeTransfers(TimeProvider clock) : IDisposable
{
    internal const int ChunkBytes = 16 * 1024;
    internal sealed record Snapshot(byte[] Bytes, string Basename, string Hash, string InstallationId) : IDisposable
    {
        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
    private sealed class Stage(string kind, int length, string hash, string basename, DateTimeOffset expires)
    {
        internal string Kind = kind, Hash = hash, Basename = basename;
        internal int Length = length;
        internal List<byte[]> Chunks = [];
        internal int Offset;
        internal DateTimeOffset Expires = expires;
    }
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private ITimer? timer;
    private Exception? backgroundFailure;
    private bool closed;
    private bool SweepLocked()
    {
        LJ.Require(!closed, "CLOSED");
        string[] expired = stages.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray();
        foreach (string key in expired) Release(key);
        return expired.Length != 0;
    }
    private void SweepAndScheduleLocked()
    {
        if (SweepLocked()) ScheduleLocked();
    }
    private void ScheduleLocked()
    {
        if (closed || stages.Count == 0)
        {
            ClearTimerLocked();
            return;
        }
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = stages.Values.Min(stage => stage.Expires);
        TimeSpan delay = expires > now ? expires - now : TimeSpan.Zero;
        if (timer is null)
            timer = clock.CreateTimer(static state => ((MusicNativeTransfers)state!).Expire(), this,
                delay, Timeout.InfiniteTimeSpan);
        else timer.Change(delay, Timeout.InfiniteTimeSpan);
    }
    private void ClearTimerLocked()
    {
        ITimer? current = timer;
        timer = null;
        if (current is null) return;
        try
        {
            current.Dispose();
        }
        catch (Exception error)
        {
            backgroundFailure ??= error;
        }
    }
    private void Expire()
    {
        lock (sync)
        {
            if (closed) return;
            try
            {
                SweepLocked();
                ScheduleLocked();
            }
            catch (Exception error)
            {
                backgroundFailure ??= error;
                foreach (string token in stages.Keys.ToArray()) Release(token);
            }
        }
    }
    private void Release(string token)
    {
        if (stages.Remove(token, out var stage))
            foreach (var chunk in stage.Chunks) CryptographicOperations.ZeroMemory(chunk);
    }
    private Stage GetLocked(string token)
    {
        SweepAndScheduleLocked();
        LJ.Require(stages.TryGetValue(token, out var value), "TRANSFER_NOT_FOUND");
        return value!;
    }
    internal JsonObject Begin(JsonObject input)
    {
        lock (sync)
        {
            SweepAndScheduleLocked(); LJ.Fields(input, "kind length sha256 basename", "kind length sha256 basename");
            string kind = LJ.Text(input["kind"]), hash = LJ.Text(input["sha256"]), basename = LJ.Text(input["basename"], 255);
            LJ.Require(kind == "script");
            long length = LJ.I(input["length"], 1);
            LJ.Require(length <= SelectedScriptSnapshot.MaximumBytes, "LIMIT");
            LJ.Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            LJ.Require(!basename.Any(c => "\\/:*?\"<>|".Contains(c)) &&
                basename.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
            string token = Guid.NewGuid().ToString("D");
            stages[token] = new(kind, (int)length, hash, basename, clock.GetUtcNow().AddMinutes(5));
            try { ScheduleLocked(); }
            catch { Release(token); throw; }
            return LJ.Obj(("token", token), ("installationId", "lx-" + token), ("chunkBytes", ChunkBytes));
        }
    }
    internal JsonObject Chunk(JsonObject input)
    {
        lock (sync)
        {
            LJ.Fields(input, "token offset data", "token offset data");
            var stage = GetLocked(LJ.Text(input["token"], 80));
            long offset = LJ.I(input["offset"]); string data = LJ.Text(input["data"], 21848);
            LJ.Require(offset == stage.Offset, "TRANSFER_OFFSET");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { throw new MusicLibraryException("INVALID_REQUEST"); }
            bool retained = false;
            try
            {
                LJ.Require(bytes.Length is > 0 and <= ChunkBytes && Convert.ToBase64String(bytes) == data &&
                    offset + bytes.Length <= stage.Length, "LIMIT");
                DateTimeOffset previousExpires = stage.Expires;
                stage.Chunks.Add(bytes); retained = true; stage.Offset += bytes.Length;
                stage.Expires = clock.GetUtcNow().AddMinutes(5);
                try { ScheduleLocked(); }
                catch
                {
                    stage.Chunks.RemoveAt(stage.Chunks.Count - 1);
                    stage.Offset -= bytes.Length;
                    stage.Expires = previousExpires;
                    retained = false;
                    throw;
                }
                return LJ.Obj(("next", stage.Offset));
            }
            finally { if (!retained) CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    internal JsonObject Cancel(JsonObject input)
    {
        lock (sync)
        {
            SweepAndScheduleLocked(); LJ.Fields(input, "token", "token");
            Release(LJ.Text(input["token"], 80)); ScheduleLocked(); return new();
        }
    }
    internal Snapshot Take(string token, string kind)
    {
        lock (sync)
        {
            var stage = GetLocked(token);
            LJ.Require(stage.Kind == kind && stage.Offset == stage.Length, "TRANSFER_INCOMPLETE");
            byte[] bytes = new byte[stage.Length];
            bool transferred = false;
            try
            {
                int offset = 0;
                foreach (var chunk in stage.Chunks) { chunk.CopyTo(bytes, offset); offset += chunk.Length; }
                Release(token);
                ScheduleLocked();
                if (LJ.Hash(bytes) != stage.Hash)
                    throw new MusicLibraryException("TRANSFER_INTEGRITY");
                transferred = true;
                return new(bytes, stage.Basename, stage.Hash, "lx-" + token);
            }
            finally { if (!transferred) CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (closed) return;
            closed = true;
            Exception? failure = backgroundFailure;
            ClearTimerLocked();
            failure ??= backgroundFailure;
            foreach (string token in stages.Keys.ToArray())
            {
                try { Release(token); }
                catch (Exception error) { failure ??= error; }
            }
            backgroundFailure = null;
            if (failure is not null) throw failure;
        }
    }
}
