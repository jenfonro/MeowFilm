using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

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
    private sealed class StageTimer(long generation)
    {
        internal readonly long Generation = generation;
        internal ITimer? Handle;
        internal bool Active = true, Armed, Fired;
    }
    private sealed record TimerCallback(WeakReference<MusicNativeTransfers> Owner, StageTimer Timer);
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly HashSet<StageTimer> timers = [];
    private readonly Dictionary<StageTimer, Exception> timerFailures = [];
    private StageTimer? timer;
    private long timerGeneration;
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
        long generation = timerGeneration + 1;
        StageTimer? next = null;
        if (!closed && stages.Count != 0)
        {
            DateTimeOffset now = clock.GetUtcNow();
            DateTimeOffset expires = stages.Values.Min(stage => stage.Expires);
            var created = new StageTimer(generation);
            timers.Add(created);
            try
            {
                var callback = new TimerCallback(new(this), created);
                created.Handle = clock.CreateTimer(static state =>
                {
                    var callback = (TimerCallback)state!;
                    if (callback.Owner.TryGetTarget(out var owner)) owner.Expire(callback.Timer);
                }, callback,
                    expires > now ? expires - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                created.Armed = true;
                next = created;
            }
            catch
            {
                if (created.Handle is null) DeactivateTimerLocked(created);
                else CancelTimerLocked(created);
                throw;
            }
        }
        StageTimer? previous = timer;
        timerGeneration = generation;
        timer = next;
        if (previous is not null && !ReferenceEquals(previous, next)) CancelTimerLocked(previous);
        foreach (StageTimer candidate in timers.ToArray())
            if (!ReferenceEquals(candidate, timer)) CancelTimerLocked(candidate);
        if (next is { Fired: true } fired) Expire(fired);
    }
    private void DeactivateTimerLocked(StageTimer value)
    {
        if (!value.Active) return;
        value.Active = false;
        timers.Remove(value);
        timerFailures.Remove(value);
        if (ReferenceEquals(timer, value)) timer = null;
    }
    private bool CancelTimerLocked(StageTimer value)
    {
        if (!value.Active) return true;
        try
        {
            value.Handle?.Dispose();
            DeactivateTimerLocked(value);
            return true;
        }
        catch (Exception error)
        {
            timerFailures[value] = error;
            return false;
        }
    }
    private void Expire(StageTimer value)
    {
        lock (sync)
        {
            if (!value.Armed) { value.Fired = true; return; }
            if (!value.Active) return;
            bool current = !closed && ReferenceEquals(timer, value) &&
                value.Generation == timerGeneration;
            if (current) timer = null;
            CancelTimerLocked(value);
            if (!current) return;
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
            int length = (int)LJ.I(input["length"], 1, Array.MaxLength);
            LJ.Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            LJ.Require(!basename.Any(c => "\\/:*?\"<>|".Contains(c)) &&
                basename.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
            string token = Guid.NewGuid().ToString("D");
            stages[token] = new(kind, length, hash, basename, clock.GetUtcNow().AddMinutes(5));
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
            timerGeneration++;
            timer = null;
            Exception? failure = backgroundFailure;
            for (int attempt = 0; attempt < 2; attempt++)
                foreach (StageTimer value in timers.ToArray())
                    CancelTimerLocked(value);
            foreach (Exception error in timerFailures.Values) failure ??= error;
            foreach (string token in stages.Keys.ToArray())
            {
                try { Release(token); }
                catch (Exception error) { failure ??= error; }
            }
            foreach (StageTimer value in timers)
            {
                value.Active = false;
                value.Handle = null;
            }
            timers.Clear();
            timerFailures.Clear();
            backgroundFailure = null;
            if (failure is not null) throw failure;
        }
    }
}
